using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Klankhuis.Hero.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Home;
using Windows.UI;
using Wavee.UI.Helpers;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Backs <c>BrowsePage</c> — a HomePage-style destination for any Spotify
/// <c>spotify:page:</c> URI. Fetches the page via Pathfinder's
/// <c>browsePage</c> persistedQuery, derives a hero carousel from the first
/// editorial section, and renders the rest as one <see cref="HomeRegion"/>
/// band of editorial shelves + a chip grid of category sub-pages + a single
/// "Explore all" CTA button.
/// </summary>
public sealed partial class BrowseViewModel : SectionFeedViewModelBase
{
    private readonly ISession? _session;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    private string _currentUri = "";

    /// <summary>The page's accent (from <c>browse.header.color.hex</c>) — passed
    /// to <see cref="HeroSlideFactory.BuildSlide"/> as <c>overrideAccent</c> so
    /// every slide of the carousel coheres with the page identity.</summary>
    public Color? HeroAccent { get; private set; }

    /// <summary>Editorial shelves wrapped in one <see cref="HomeRegion"/> band
    /// (eyebrow + header + mica gradient), rendered via <c>HomeRegionView</c>
    /// to match the homepage's region treatment.</summary>
    [ObservableProperty]
    private ObservableCollection<HomeRegion> _pageRegions = new();

    /// <summary>Chip groups extracted from the response's
    /// <c>BrowseGridSectionData</c> — bucketed via
    /// <see cref="BrowseAllGrouper"/> into TOP / FOR YOU / GENRES / etc.</summary>
    [ObservableProperty]
    private IList<BrowseAllGroup> _browseGroups = new List<BrowseAllGroup>();

    /// <summary>Single "Explore all categories" CTA extracted from the
    /// response's <c>BrowseRelatedSectionData</c>. Rendered as a separate
    /// button below the chip groups (NOT folded into the grid).</summary>
    [ObservableProperty]
    private BrowseCta? _browseCta;

    public ICommand HeroPlayCommand { get; }
    public ICommand HeroOpenCommand { get; }
    public ICommand NavigateToCtaCommand { get; }

    public BrowseViewModel(
        ISession? session = null,
        ILogger<BrowseViewModel>? logger = null)
    {
        _session = session;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        HeroPlayCommand = new RelayCommand<HomeSectionItem>(PlayItemSafe);
        HeroOpenCommand = new RelayCommand<HomeSectionItem>(NavigateToItemSafe);
        NavigateToCtaCommand = new RelayCommand(NavigateToCtaSafe);
    }

    /// <summary>Page entry point — called from <c>BrowsePage.OnNavigatedTo</c>.</summary>
    public async Task LoadAsync(ContentNavigationParameter? parameter)
    {
        if (parameter is null || string.IsNullOrEmpty(parameter.Uri)) return;

        // Re-entry on the same URI with data already loaded → no-op
        // (avoids flicker on nav-back). Either editorial sections OR chip
        // groups counts as "already loaded" — some pages have only one.
        var hasContent = Sections.Count > 0 || BrowseGroups.Count > 0;
        if (CurrentUri == parameter.Uri && hasContent && !IsLoading) return;

        CurrentUri = parameter.Uri;
        Title = parameter.Title;
        Subtitle = parameter.Subtitle;

        await ReloadAsync();
    }

    /// <inheritdoc />
    public override async Task ReloadAsync() => await ReloadCoreAsync();

    [RelayCommand]
    private async Task ReloadCoreAsync()
    {
        if (_session is null || !_session.IsConnected())
        {
            HasError = true;
            ErrorMessage = "Not connected.";
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            var response = await _session.Pathfinder.GetBrowsePageAsync(CurrentUri).ConfigureAwait(false);

            // Header — title + accent. Headers can be missing on some pages
            // (sub-page xlink targets); fall back gracefully.
            var headerTitle = response?.Data?.Browse?.Header?.Title?.TransformedLabel
                              ?? response?.Data?.Browse?.Data?.CardRepresentation?.Title?.TransformedLabel;
            var headerHex = response?.Data?.Browse?.Header?.Color?.Hex
                            ?? response?.Data?.Browse?.Data?.CardRepresentation?.BackgroundColor?.Hex;

            // Map sections via the shared mapper. Returns three structurally
            // different surfaces: editorial shelves, chip-grid groups, single CTA.
            var mapped = BrowseResponseMapper.MapSections(response);

            // Derive hero slides from the first qualifying editorial section,
            // then remove that section so it doesn't double-display below.
            var (slides, leftover) = DeriveHeroSlides(mapped.Editorial, headerHex);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrEmpty(headerTitle))
                    Title = headerTitle;

                HeaderBackdropBrush = BuildHeaderBrush(headerHex);
                HeroAccent = TintColorHelper.TryParseHex(headerHex, out var c) ? c : null;

                HeroSlides = slides;

                // Backwards-compat: keep `Sections` populated with the leftover
                // editorial shelves so the re-entry guard in LoadAsync can read
                // it as a "have data" signal.
                Sections.Clear();
                foreach (var section in leftover)
                    Sections.Add(section);

                // Build ONE region wrapping the editorial shelves. Eyebrow +
                // header + accent gradient give Browse the same band treatment
                // as Home regions, without applying Home's 4-region classifier
                // (which doesn't fit Browse content).
                var newRegions = new ObservableCollection<HomeRegion>();
                if (leftover.Count > 0)
                {
                    var accent = HeroAccent ?? Color.FromArgb(255, 0x60, 0xCD, 0xFF);
                    var region = new HomeRegion
                    {
                        Kind = HomeRegionKind.Discover,
                        Eyebrow = "EDITORIAL",
                        Header = "Recommendations",
                        AccentColor = accent
                    };
                    foreach (var section in leftover)
                        region.Sections.Add(section);
                    newRegions.Add(region);
                }
                PageRegions = newRegions;

                BrowseGroups = mapped.BrowseGroups;
                BrowseCta = mapped.Cta;

                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load browse page {Uri}", CurrentUri);
            _dispatcherQueue.TryEnqueue(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
                IsLoading = false;
            });
        }
    }

    /// <summary>
    /// Walk sections in order, pick the FIRST qualifying section as the
    /// carousel source. "Qualifying" = ≥ 3 items AND items aren't in-page
    /// sub-page tiles (categorical, not editorial) AND every item resolves
    /// to a real HTTPS image URL. Take up to 5 items as slides; remove the
    /// source section from the leftover list.
    /// </summary>
    private (IList<HeroCarouselItem> Slides, List<HomeSection> Leftover) DeriveHeroSlides(
        List<HomeSection> sections, string? headerHex)
    {
        var slides = new List<HeroCarouselItem>();
        var sourceIndex = -1;

        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];

            if (section.Items.Count < 3)
            {
                _logger?.LogDebug("[hero] section[{Index}] '{Title}' skipped: {Count} items < 3 minimum",
                    i, section.Title, section.Items.Count);
                continue;
            }

            // Skip sub-page tile grids — those items have ColorHex set and
            // ContentType==Unknown (per BrowseResponseMapper.MapBrowseSubpage).
            // Categorical, not editorial — wouldn't make a meaningful carousel.
            // (Note: with the new mapper, BrowseGridSectionData is routed away
            // from `editorial` entirely, so this check now mostly catches
            // edge cases where a sub-page item slips into a non-grid section.)
            var isSubpageGrid = section.Items.All(it =>
                it.ContentType == HomeContentType.Unknown && !string.IsNullOrEmpty(it.ColorHex));
            if (isSubpageGrid)
            {
                _logger?.LogDebug("[hero] section[{Index}] '{Title}' skipped: all items are sub-page tiles",
                    i, section.Title);
                continue;
            }

            // Klankhuis HeroCarousel.BakeAllAsync `continue`s when ImageUri is
            // null — the slide then renders pure black (no backdrop, no accent
            // fill, just the template's #0A0810). Reject items whose ImageUrl
            // won't resolve to a real HTTPS URL (mosaic URIs are non-empty
            // strings but ToHttpsUrl returns null for them — IsNullOrEmpty
            // doesn't catch that).
            var resolvable = section.Items.Count(it => SpotifyImageHelper.CanResolveToHttpsUrl(it.ImageUrl));
            if (resolvable < section.Items.Count)
            {
                _logger?.LogDebug("[hero] section[{Index}] '{Title}' skipped: {Resolvable}/{Total} items resolve to https",
                    i, section.Title, resolvable, section.Items.Count);
                continue;
            }

            sourceIndex = i;
            break;
        }

        if (sourceIndex < 0)
        {
            _logger?.LogInformation("[hero] no qualifying section found in {Total} editorial sections — carousel will be empty",
                sections.Count);
            return (slides, sections);
        }

        var sourceSection = sections[sourceIndex];
        var eyebrow = (sourceSection.Title ?? string.Empty).ToUpperInvariant();
        var overrideAccent = TintColorHelper.TryParseHex(headerHex, out var c) ? c : (Color?)null;

        foreach (var item in sourceSection.Items.Take(5))
        {
            slides.Add(HeroSlideFactory.BuildSlide(
                item,
                eyebrow,
                primaryCta: "Play",
                secondaryCta: "Open",
                primaryCommand: HeroPlayCommand,
                secondaryCommand: HeroOpenCommand,
                overrideAccent: overrideAccent));
        }

        _logger?.LogInformation("[hero] source: section[{Index}] '{Title}', {Slides} slides",
            sourceIndex, sourceSection.Title, slides.Count);

        // Remove source section from the rendered list
        var leftover = new List<HomeSection>(sections);
        leftover.RemoveAt(sourceIndex);
        return (slides, leftover);
    }

    /// <summary>
    /// Vertical gradient header backdrop — full tint at top, fading to
    /// transparent before the first shelf. Mirrors HomePage's palette wash.
    /// </summary>
    private static Brush? BuildHeaderBrush(string? hex)
    {
        if (!TintColorHelper.TryParseHex(hex, out var raw)) return null;
        var lifted = TintColorHelper.BrightenForTint(raw, targetMax: 220);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Color.FromArgb(0xC8, lifted.R, lifted.G, lifted.B),
            Offset = 0.0
        });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Color.FromArgb(0x60, lifted.R, lifted.G, lifted.B),
            Offset = 0.55
        });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Color.FromArgb(0x00, lifted.R, lifted.G, lifted.B),
            Offset = 1.0
        });
        return brush;
    }

    private static void PlayItemSafe(HomeSectionItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Uri)) return;
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        var uri = item.Uri!;
        _ = Task.Run(() => playback.PlayContextAsync(uri));
    }

    private static void NavigateToItemSafe(HomeSectionItem? item)
    {
        if (item is null) return;
        HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
    }

    private void NavigateToCtaSafe()
    {
        var cta = BrowseCta;
        if (cta is null || string.IsNullOrEmpty(cta.Uri)) return;
        // Same navigation path BrowseChip uses for spotify:page: URIs.
        HomeViewModel.NavigateToItem(new HomeSectionItem { Uri = cta.Uri }, NavigationHelpers.IsCtrlPressed());
    }
}
