using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Owns recommendation enrichment for the home feed:
/// <list type="bullet">
///   <item><b>Baseline enrichment</b> — async fetch of preview tracks /
///         canvases / hero colours for baseline-section items, applied in
///         place on the bound <see cref="HomeSection"/> snapshots.</item>
///   <item><b>Recently-played hand-off</b> — dispatches the parsed Recents
///         section's items into <see cref="RecentlyPlayedService"/> on every
///         home parse.</item>
///   <item><b>Featured item tracking</b> — surfaces the most-recently-played
///         item to the parent so the hero card slot ("Pick up where you left
///         off") and palette pipeline stay in sync.</item>
/// </list>
///
/// <para>This child does NOT own the Sections collection — it operates on a
/// snapshot accessor passed in by the parent. Enrichment writes back into the
/// HomeSection.Items in place, so the parent's bound collection observes the
/// per-item PropertyChanged notifications without needing a re-assignment.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class HomeRecommendationsViewModel : ObservableObject, IDisposable
{
    private readonly IHomeFeedService? _homeFeedService;
    private readonly HomeFeedCache? _homeFeedCache;
    private readonly RecentlyPlayedService? _recentlyPlayedService;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<ObservableCollection<HomeSection>> _sectionsProvider;

    private CancellationTokenSource? _baselineEnrichmentCts;
    private int _baselineEnrichmentVersion;
    private bool _disposed;
    private bool _attached;

    /// <summary>Most-recently-played item promoted to the hero card slot
    /// on the right of the greeting band. Drives the parent's palette fetch.</summary>
    [ObservableProperty]
    public partial HomeSectionItem? FeaturedItem { get; set; }

    /// <summary>Raised after <see cref="FeaturedItem"/> changes so the parent
    /// can re-derive its hero palette / backdrop brushes. Parent listens on
    /// the partial-method hook rather than direct cross-child references.</summary>
    public event EventHandler? FeaturedItemChanged;

    partial void OnFeaturedItemChanged(HomeSectionItem? value)
        => FeaturedItemChanged?.Invoke(this, EventArgs.Empty);

    public HomeRecommendationsViewModel(
        IHomeFeedService? homeFeedService,
        HomeFeedCache? homeFeedCache,
        RecentlyPlayedService? recentlyPlayedService,
        ILogger? logger,
        Func<ObservableCollection<HomeSection>> sectionsProvider)
    {
        _homeFeedService = homeFeedService;
        _homeFeedCache = homeFeedCache;
        _recentlyPlayedService = recentlyPlayedService;
        _logger = logger;
        _sectionsProvider = sectionsProvider;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    private ObservableCollection<HomeSection> Sections => _sectionsProvider();

    // ── Recently-played service wiring ───────────────────────────────────────

    /// <summary>
    /// Subscribe to <see cref="RecentlyPlayedService.ItemsChanged"/> so the
    /// featured-item hero slot updates whenever the user plays something new.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public void AttachRecentlyPlayedListener()
    {
        if (_attached || _recentlyPlayedService is null) return;
        _attached = true;
        _recentlyPlayedService.ItemsChanged += OnRecentlyPlayedItemsChanged;
    }

    public void DetachRecentlyPlayedListener()
    {
        if (!_attached || _recentlyPlayedService is null) return;
        _attached = false;
        _recentlyPlayedService.ItemsChanged -= OnRecentlyPlayedItemsChanged;
    }

    private void OnRecentlyPlayedItemsChanged()
    {
        if (_disposed || _recentlyPlayedService == null) return;

        // Must dispatch to UI thread — this event can fire from background threads
        // and ObservableCollection mutations must happen on the UI thread.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;

            var items = _recentlyPlayedService.Items;
            if (items.Count == 0) return;

            // Promote the most-recently-played item to the hero card slot.
            // FeaturedItem's setter triggers FeaturedItemChanged via the
            // partial-method hook above; the parent re-derives the hero
            // backdrop wash from the cover (album/playlist Pathfinder palette
            // route).
            FeaturedItem = items[0];

            // ── Note: we DO NOT mutate the Recents section in `Sections`
            // here anymore. The parser owns that section now (built from
            // HomeRecentlyPlayedSectionData on every Home parse), and
            // HomeFeedCache.ApplyDiff keeps its items current via its
            // SectionUri-keyed diff. Touching Sections here was racing with
            // ApplyDiff on nav-back and producing the symptom where the
            // Recents row briefly showed items from a different section.
            // The standalone StartPage carousel + the FeaturedItem hero
            // both still get fed from the service via this same event.
        });
    }

    /// <summary>
    /// Hand the parsed Recents section's items to <see cref="RecentlyPlayedService"/>.
    /// Called from every code path that produces section data (cache hit, fresh
    /// fetch, direct fetch, refetch-with-facet) so the carousel + featured-item
    /// hero stay in sync with whatever the freshest Home response carried.
    /// Safe to call with sections that have no Recents entry — no-ops in that case.
    /// </summary>
    public void DispatchRecentsToService(List<HomeSection> sections)
    {
        if (_recentlyPlayedService == null) return;
        var recents = sections.FirstOrDefault(s => s.SectionType == HomeSectionType.RecentlyPlayed);
        if (recents == null) return;
        _recentlyPlayedService.ApplyHomeRecents(recents.Items);
    }

    // ── Baseline enrichment ─────────────────────────────────────────────────

    public void CancelBaselineEnrichment()
    {
        _baselineEnrichmentVersion++;
        _baselineEnrichmentCts?.Cancel();
        _baselineEnrichmentCts?.Dispose();
        _baselineEnrichmentCts = null;
    }

    public void BeginBaselineEnrichment()
    {
        if (_homeFeedService is null || !_homeFeedService.IsAvailable) return;

        CancelBaselineEnrichment();

        var baselineItems = Sections
            .Where(section => section.SectionType == HomeSectionType.Baseline)
            .SelectMany(section => section.Items)
            .Where(item => !item.HasBaselinePreview
                           && !string.IsNullOrWhiteSpace(item.Uri)
                           && item.ContentType is HomeContentType.Playlist or HomeContentType.Album)
            .ToList();

        if (baselineItems.Count == 0)
        {
            ClearLoadingForBaselineItems(Sections);
            return;
        }

        foreach (var item in baselineItems)
            item.IsBaselineLoading = true;

        var uris = baselineItems
            .Select(item => item.Uri!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var version = ++_baselineEnrichmentVersion;
        var cts = new CancellationTokenSource();
        _baselineEnrichmentCts = cts;
        _ = EnrichBaselineItemsAsync(uris, version, cts.Token);
    }

    private async Task EnrichBaselineItemsAsync(List<string> uris, int version, CancellationToken ct)
    {
        try
        {
            if (_homeFeedService is null) return;
            var response = await _homeFeedService.GetFeedBaselineLookupAsync(uris, ct).ConfigureAwait(false);
            if (response is null) return;
            var lookup = BuildBaselineEnrichmentLookup(response);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested || version != _baselineEnrichmentVersion)
                    return;

                ApplyBaselineEnrichment(Sections, lookup);

                var cached = _homeFeedCache?.GetCached();
                if (cached != null)
                    ApplyBaselineEnrichment(cached.Sections, lookup);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich home baseline sections");
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (version == _baselineEnrichmentVersion)
                    ClearLoadingForBaselineItems(Sections);
            });
        }
    }

    private static Dictionary<string, HomeBaselineEnrichment> BuildBaselineEnrichmentLookup(
        FeedBaselineLookupResponse response)
    {
        var result = new Dictionary<string, HomeBaselineEnrichment>(StringComparer.Ordinal);
        var entries = response.Data?.Lookup;
        if (entries == null) return result;

        foreach (var entry in entries)
        {
            var previewItems = entry.TypeName switch
            {
                "PlaylistResponseWrapper" => entry.GetPlaylistData()?.PreviewItems,
                "AlbumResponseWrapper" => entry.GetAlbumData()?.PreviewItems,
                _ => null
            };

            var tracks = previewItems?.Items?
                .Select(wrapper => wrapper.Data)
                .Where(track => track != null)
                .Select(track => MapBaselinePreviewTrack(track!))
                .Where(track => !string.IsNullOrWhiteSpace(track.Uri) || !string.IsNullOrWhiteSpace(track.Name))
                .ToList() ?? [];

            var uri = entry.TypeName switch
            {
                "PlaylistResponseWrapper" => entry.GetPlaylistData()?.Uri ?? entry.Uri,
                "AlbumResponseWrapper" => entry.GetAlbumData()?.Uri ?? entry.Uri,
                _ => entry.Uri
            };

            if (string.IsNullOrWhiteSpace(uri))
                continue;

            var primary = tracks.FirstOrDefault();
            result[uri] = new HomeBaselineEnrichment(
                uri,
                tracks,
                primary?.CanvasThumbnailUrl ?? primary?.CoverArtUrl,
                primary?.ColorHex,
                primary?.CanvasUrl,
                primary?.CanvasThumbnailUrl,
                primary?.AudioPreviewUrl);
        }

        return result;
    }

    private static HomeBaselinePreviewTrack MapBaselinePreviewTrack(FeedBaselineTrackData track)
    {
        var cover = track.AlbumOfTrack?.CoverArt;
        var coverUrl = cover?.Sources?
            .OrderByDescending(source => source.Width ?? 0)
            .FirstOrDefault()?.Url;

        var canvasThumbnail = PickCanvasThumbnail(track.Canvas?.Thumbnail?.Sources);

        return new HomeBaselinePreviewTrack
        {
            Uri = track.Uri,
            Name = track.Name,
            CoverArtUrl = coverUrl,
            ColorHex = cover?.ExtractedColors?.ColorDark?.Hex,
            CanvasUrl = track.Canvas?.Url,
            CanvasThumbnailUrl = canvasThumbnail,
            AudioPreviewUrl = track.Previews?.AudioPreviews?.Items?.FirstOrDefault()?.Url
        };
    }

    private static string? PickCanvasThumbnail(IReadOnlyList<FeedBaselineCanvasThumbnailSource>? sources)
    {
        if (sources == null || sources.Count == 0) return null;

        return sources.FirstOrDefault(source =>
                   source.Url?.Contains("288x512", StringComparison.OrdinalIgnoreCase) == true)?.Url
               ?? sources.LastOrDefault(source => !string.IsNullOrWhiteSpace(source.Url))?.Url
               ?? sources.FirstOrDefault()?.Url;
    }

    private static void ApplyBaselineEnrichment(
        IEnumerable<HomeSection> sections,
        IReadOnlyDictionary<string, HomeBaselineEnrichment> lookup)
    {
        foreach (var item in sections
                     .Where(section => section.SectionType == HomeSectionType.Baseline)
                     .SelectMany(section => section.Items))
        {
            if (item.Uri != null && lookup.TryGetValue(item.Uri, out var enrichment))
            {
                item.PreviewTracks = enrichment.PreviewTracks;
                item.HeroImageUrl = enrichment.HeroImageUrl ?? item.ImageUrl;
                item.HeroColorHex = enrichment.HeroColorHex ?? item.ColorHex;
                item.CanvasUrl = enrichment.CanvasUrl;
                item.CanvasThumbnailUrl = enrichment.CanvasThumbnailUrl;
                item.AudioPreviewUrl = enrichment.AudioPreviewUrl;
                item.HasBaselinePreview = enrichment.PreviewTracks.Count > 0;
            }
            else
            {
                item.HeroImageUrl ??= item.ImageUrl;
                item.HeroColorHex ??= item.ColorHex;
            }

            item.IsBaselineLoading = false;
        }
    }

    private static void ClearLoadingForBaselineItems(IEnumerable<HomeSection> sections)
    {
        foreach (var item in sections
                     .Where(section => section.SectionType == HomeSectionType.Baseline)
                     .SelectMany(section => section.Items))
        {
            item.HeroImageUrl ??= item.ImageUrl;
            item.HeroColorHex ??= item.ColorHex;
            item.IsBaselineLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachRecentlyPlayedListener();
        CancelBaselineEnrichment();
    }
}
