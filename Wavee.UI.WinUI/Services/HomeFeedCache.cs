using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Snapshot of the home feed data.
/// </summary>
public sealed record HomeFeedSnapshot(string? Greeting, List<HomeSection> Sections, List<HomeChipViewModel>? Chips = null);

/// <summary>
/// Singleton cache for the home feed. Extends <see cref="PageCache{TSnapshot}"/> with
/// home-specific diff logic for sections and items.
/// </summary>
public sealed class HomeFeedCache : PageCache<HomeFeedSnapshot>, IHomeFeedCache
{
    private readonly HomeResponseParserFactory _parserFactory;

    /// <summary>Current facet filter (chip id). Null or empty = no filter.</summary>
    public string? CurrentFacet { get; set; }

    public HomeFeedCache(HomeResponseParserFactory? parserFactory = null, ILogger<HomeFeedCache>? logger = null) : base(logger)
    {
        _parserFactory = parserFactory ?? new HomeResponseParserFactory();
    }

    protected override async Task<HomeFeedSnapshot> FetchCoreAsync(ISession session, CancellationToken ct)
    {
        var response = await session.Pathfinder.GetHomeAsync(sectionItemsLimit: 10, facet: CurrentFacet, ct: ct)
            .ConfigureAwait(false);

        var result = await Task.Run(() => _parserFactory.Parse(response), ct).ConfigureAwait(false);
        PreserveDisplayDataFromPreviousSnapshot(result.Sections, GetCached()?.Sections);
        RemoveUnrenderableItems(result.Sections);

        // Only use chips from unfaceted (default) responses
        List<HomeChipViewModel>? chips = string.IsNullOrEmpty(CurrentFacet) ? result.Chips : null;

        Logger?.LogDebug("Home feed cached: {Count} sections, facet={Facet}", result.Sections.Count, CurrentFacet ?? "(none)");
        return new HomeFeedSnapshot(result.Greeting, result.Sections, chips);
    }

    private static List<HomeChipViewModel>? MapChips(List<HomeChip>? apiChips)
    {
        if (apiChips == null || apiChips.Count == 0) return null;

        var chips = new List<HomeChipViewModel>
        {
            // "All" chip — represents no filter (default state), always first
            new() { Id = "", Label = "All", IsSelected = true }
        };

        chips.AddRange(apiChips.Select(c => new HomeChipViewModel
        {
            Id = c.Id ?? "",
            Label = c.Label?.TransformedLabel ?? c.Id ?? "",
            SubChips = c.SubChips?.Select(sc => new HomeChipViewModel
            {
                Id = sc.Id ?? "",
                Label = sc.Label?.TransformedLabel ?? sc.Id ?? ""
            }).ToList() ?? []
        }));

        return chips;
    }

    // ── Incremental diff engine ──

    /// <summary>
    /// Applies incremental changes from fresh data to the current observable collection.
    /// Preserves scroll position and enables smooth animations.
    /// </summary>
    public static void ApplyDiff(
        ObservableCollection<HomeSection> current,
        List<HomeSection> fresh,
        Action<string?>? onGreetingChanged = null,
        string? newGreeting = null,
        Action<HomeSection>? onAccentChanged = null)
    {
        if (onGreetingChanged != null && newGreeting != null)
            onGreetingChanged(newGreeting);

        // Build lookup from current sections
        var currentByUri = new Dictionary<string, int>();
        for (int i = 0; i < current.Count; i++)
            currentByUri[current[i].SectionUri] = i;

        var freshUris = new HashSet<string>(fresh.Select(s => s.SectionUri));

        // 1. Remove sections no longer in fresh (iterate backwards to preserve indices)
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!freshUris.Contains(current[i].SectionUri))
                current.RemoveAt(i);
        }

        // 2. Add new sections and update existing ones, maintaining fresh order
        for (int i = 0; i < fresh.Count; i++)
        {
            if (i < current.Count && current[i].SectionUri == fresh[i].SectionUri)
            {
                // Same position — diff items in-place
                UpdateSectionInPlace(current[i], fresh[i], onAccentChanged);
            }
            else
            {
                // Check if section exists elsewhere in current (needs reorder)
                var existingIdx = -1;
                for (int j = 0; j < current.Count; j++)
                {
                    if (current[j].SectionUri == fresh[i].SectionUri)
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx >= 0)
                {
                    // Move existing section to correct position
                    current.Move(existingIdx, Math.Min(i, current.Count - 1));
                    UpdateSectionInPlace(current[i], fresh[i], onAccentChanged);
                }
                else
                {
                    // New section — insert at correct position
                    current.Insert(Math.Min(i, current.Count), fresh[i]);
                }
            }
        }

        // 3. Trim any excess sections (shouldn't happen often)
        while (current.Count > fresh.Count)
            current.RemoveAt(current.Count - 1);
    }

    private static void UpdateSectionInPlace(
        HomeSection target,
        HomeSection source,
        Action<HomeSection>? onAccentChanged = null)
    {
        DiffItems(target.Items, source.Items);
        if (target.Title != source.Title) target.Title = source.Title;
        if (target.Subtitle != source.Subtitle) target.Subtitle = source.Subtitle;
        if (target.SectionType != source.SectionType) target.SectionType = source.SectionType;
        if (target.RawSpotifyJson != source.RawSpotifyJson) target.RawSpotifyJson = source.RawSpotifyJson;
        if (target.HeaderEntityName != source.HeaderEntityName) target.HeaderEntityName = source.HeaderEntityName;
        if (target.HeaderEntityImageUrl != source.HeaderEntityImageUrl) target.HeaderEntityImageUrl = source.HeaderEntityImageUrl;
        if (target.HeaderEntityUri != source.HeaderEntityUri) target.HeaderEntityUri = source.HeaderEntityUri;

        // Visual-identity flags driven by item composition. Without this
        // propagation the target's stale TRUE survives a mixed-content refresh
        // and the section header keeps its podcast-purple wash + mic glyph
        // even after Spotify swaps the items to playlists.
        if (target.IsPodcastSection != source.IsPodcastSection)
            target.IsPodcastSection = source.IsPodcastSection;

        var accentChanged = target.AccentColorHex != source.AccentColorHex;
        if (accentChanged)
        {
            target.AccentColorHex = source.AccentColorHex;
            // The brushes derived from AccentColorHex (AccentLineBrush etc.)
            // are theme-dependent — only the caller knows isDark, so it
            // re-applies theme on the section via this callback.
            onAccentChanged?.Invoke(target);
        }
    }

    /// <summary>
    /// Diffs items within a single section. Updates in-place, adds new, removes old.
    /// </summary>
    private static void DiffItems(
        ObservableCollection<HomeSectionItem> current,
        ObservableCollection<HomeSectionItem> fresh)
    {
        var freshByUri = new Dictionary<string, int>();
        for (int i = 0; i < fresh.Count; i++)
        {
            if (fresh[i].Uri != null)
                freshByUri[fresh[i].Uri!] = i;
        }

        // Remove items no longer present
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (current[i].Uri != null && !freshByUri.ContainsKey(current[i].Uri!))
                current.RemoveAt(i);
        }

        // Add/update items in correct order
        for (int i = 0; i < fresh.Count; i++)
        {
            if (i < current.Count && current[i].Uri == fresh[i].Uri)
            {
                // Same item — update properties if changed.
                // ContentType drift forces a Replace: a DataTemplateSelector
                // is consulted only at element create/recycle time, never on
                // property change, so a mutated ContentType would leave the
                // wrong-shape card mounted forever (the home shelf bug).
                if (ShouldReplaceForContentType(current[i], fresh[i]))
                {
                    current.RemoveAt(i);
                    current.Insert(i, fresh[i]);
                }
                else
                {
                    UpdateItemInPlace(current[i], fresh[i]);
                }
            }
            else
            {
                // Check if exists elsewhere
                var existingIdx = -1;
                for (int j = i; j < current.Count; j++)
                {
                    if (current[j].Uri == fresh[i].Uri)
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx >= 0)
                {
                    current.Move(existingIdx, i);
                    if (ShouldReplaceForContentType(current[i], fresh[i]))
                    {
                        current.RemoveAt(i);
                        current.Insert(i, fresh[i]);
                    }
                    else
                    {
                        UpdateItemInPlace(current[i], fresh[i]);
                    }
                }
                else
                {
                    current.Insert(Math.Min(i, current.Count), fresh[i]);
                }
            }
        }

        // Trim excess
        while (current.Count > fresh.Count)
            current.RemoveAt(current.Count - 1);
    }

    private static void UpdateItemInPlace(HomeSectionItem target, HomeSectionItem source)
    {
        SetStringPreservingExisting(target.Title, source.Title, v => target.Title = v);
        SetStringPreservingExisting(target.Subtitle, source.Subtitle, v => target.Subtitle = v);
        SetStringPreservingExisting(target.ImageUrl, source.ImageUrl, v => target.ImageUrl = v);
        SetStringPreservingExisting(target.ColorHex, source.ColorHex, v => target.ColorHex = v);
        if (target.ContentType != source.ContentType
            && (source.ContentType != HomeContentType.Unknown || target.ContentType == HomeContentType.Unknown))
            target.ContentType = source.ContentType;
        SetStringPreservingExisting(target.PlaceholderGlyph, source.PlaceholderGlyph, v => target.PlaceholderGlyph = v);
        if (target.IsBaselineLoading != source.IsBaselineLoading) target.IsBaselineLoading = source.IsBaselineLoading;
        if (target.HasBaselinePreview != source.HasBaselinePreview) target.HasBaselinePreview = source.HasBaselinePreview;
        SetStringPreservingExisting(target.HeroImageUrl, source.HeroImageUrl, v => target.HeroImageUrl = v);
        SetStringPreservingExisting(target.HeroColorHex, source.HeroColorHex, v => target.HeroColorHex = v);
        SetStringPreservingExisting(target.CanvasUrl, source.CanvasUrl, v => target.CanvasUrl = v);
        SetStringPreservingExisting(target.CanvasThumbnailUrl, source.CanvasThumbnailUrl, v => target.CanvasThumbnailUrl = v);
        SetStringPreservingExisting(target.AudioPreviewUrl, source.AudioPreviewUrl, v => target.AudioPreviewUrl = v);
        SetStringPreservingExisting(target.BaselineGroupTitle, source.BaselineGroupTitle, v => target.BaselineGroupTitle = v);
        if (!ReferenceEquals(target.PreviewTracks, source.PreviewTracks)) target.PreviewTracks = source.PreviewTracks;
        if (target.RecentlyAddedCount != source.RecentlyAddedCount) target.RecentlyAddedCount = source.RecentlyAddedCount;
        if (target.IsRecentlySaved != source.IsRecentlySaved) target.IsRecentlySaved = source.IsRecentlySaved;
        if (!ReferenceEquals(target.RecentlyAddedThumbnailUris, source.RecentlyAddedThumbnailUris)) target.RecentlyAddedThumbnailUris = source.RecentlyAddedThumbnailUris;
        SetStringPreservingExisting(target.RecentlyAddedThumbnail1Url, source.RecentlyAddedThumbnail1Url, v => target.RecentlyAddedThumbnail1Url = v);
        SetStringPreservingExisting(target.RecentlyAddedThumbnail2Url, source.RecentlyAddedThumbnail2Url, v => target.RecentlyAddedThumbnail2Url = v);
        SetStringPreservingExisting(target.RecentlyAddedThumbnail3Url, source.RecentlyAddedThumbnail3Url, v => target.RecentlyAddedThumbnail3Url = v);
        if (target.DurationMs != source.DurationMs) target.DurationMs = source.DurationMs;
        if (target.PlayedPositionMs != source.PlayedPositionMs) target.PlayedPositionMs = source.PlayedPositionMs;
        if (target.PlayedState != source.PlayedState) target.PlayedState = source.PlayedState;
        SetStringPreservingExisting(target.PublisherName, source.PublisherName, v => target.PublisherName = v);
        if (target.IsVideoPodcast != source.IsVideoPodcast) target.IsVideoPodcast = source.IsVideoPodcast;
        SetStringPreservingExisting(target.ReleaseDateIso, source.ReleaseDateIso, v => target.ReleaseDateIso = v);
    }

    private static bool ShouldReplaceForContentType(HomeSectionItem current, HomeSectionItem fresh)
    {
        if (current.ContentType == fresh.ContentType)
            return false;

        // Sparse Home responses can temporarily lose the entity trait that
        // identifies playlists/albums/artists. Do not replace a known-template
        // card with an Unknown card for the same URI; preserve the existing
        // template until a complete response arrives.
        if (fresh.ContentType == HomeContentType.Unknown && current.ContentType != HomeContentType.Unknown)
            return false;

        return true;
    }

    private static void PreserveDisplayDataFromPreviousSnapshot(
        List<HomeSection> freshSections,
        List<HomeSection>? previousSections)
    {
        if (previousSections is not { Count: > 0 }) return;

        var previousByUri = new Dictionary<string, HomeSectionItem>(StringComparer.Ordinal);
        foreach (var item in previousSections.SelectMany(section => section.Items))
        {
            if (!string.IsNullOrWhiteSpace(item.Uri) && !previousByUri.ContainsKey(item.Uri))
                previousByUri.Add(item.Uri, item);
        }

        if (previousByUri.Count == 0) return;

        foreach (var item in freshSections.SelectMany(section => section.Items))
        {
            if (string.IsNullOrWhiteSpace(item.Uri) || !previousByUri.TryGetValue(item.Uri, out var previous))
                continue;

            FillMissingDisplayData(item, previous);
        }
    }

    private static void FillMissingDisplayData(HomeSectionItem target, HomeSectionItem source)
    {
        if (target.ContentType == HomeContentType.Unknown && source.ContentType != HomeContentType.Unknown)
            target.ContentType = source.ContentType;

        SetMissingString(target.Title, source.Title, v => target.Title = v);
        SetMissingString(target.Subtitle, source.Subtitle, v => target.Subtitle = v);
        SetMissingString(target.ImageUrl, source.ImageUrl, v => target.ImageUrl = v);
        SetMissingString(target.ColorHex, source.ColorHex, v => target.ColorHex = v);
        SetMissingString(target.PlaceholderGlyph, source.PlaceholderGlyph, v => target.PlaceholderGlyph = v);
        SetMissingString(target.HeroImageUrl, source.HeroImageUrl, v => target.HeroImageUrl = v);
        SetMissingString(target.HeroColorHex, source.HeroColorHex, v => target.HeroColorHex = v);
        SetMissingString(target.CanvasUrl, source.CanvasUrl, v => target.CanvasUrl = v);
        SetMissingString(target.CanvasThumbnailUrl, source.CanvasThumbnailUrl, v => target.CanvasThumbnailUrl = v);
        SetMissingString(target.AudioPreviewUrl, source.AudioPreviewUrl, v => target.AudioPreviewUrl = v);
        SetMissingString(target.BaselineGroupTitle, source.BaselineGroupTitle, v => target.BaselineGroupTitle = v);
        SetMissingString(target.PublisherName, source.PublisherName, v => target.PublisherName = v);
        SetMissingString(target.ReleaseDateIso, source.ReleaseDateIso, v => target.ReleaseDateIso = v);

        if (!target.HasBaselinePreview && source.HasBaselinePreview)
            target.HasBaselinePreview = true;
        if (target.PreviewTracks.Count == 0 && source.PreviewTracks.Count > 0)
            target.PreviewTracks = source.PreviewTracks;
        if (target.RecentlyAddedCount is null && source.RecentlyAddedCount is not null)
            target.RecentlyAddedCount = source.RecentlyAddedCount;
        if (!target.IsRecentlySaved && source.IsRecentlySaved)
            target.IsRecentlySaved = true;
        if (target.RecentlyAddedThumbnailUris.Count == 0 && source.RecentlyAddedThumbnailUris.Count > 0)
            target.RecentlyAddedThumbnailUris = source.RecentlyAddedThumbnailUris;
        SetMissingString(target.RecentlyAddedThumbnail1Url, source.RecentlyAddedThumbnail1Url, v => target.RecentlyAddedThumbnail1Url = v);
        SetMissingString(target.RecentlyAddedThumbnail2Url, source.RecentlyAddedThumbnail2Url, v => target.RecentlyAddedThumbnail2Url = v);
        SetMissingString(target.RecentlyAddedThumbnail3Url, source.RecentlyAddedThumbnail3Url, v => target.RecentlyAddedThumbnail3Url = v);
        if (target.DurationMs is null && source.DurationMs is not null)
            target.DurationMs = source.DurationMs;
        if (target.PlayedPositionMs is null && source.PlayedPositionMs is not null)
            target.PlayedPositionMs = source.PlayedPositionMs;
        if (target.PlayedState is null && source.PlayedState is not null)
            target.PlayedState = source.PlayedState;
        if (!target.IsVideoPodcast && source.IsVideoPodcast)
            target.IsVideoPodcast = true;
    }

    private static void RemoveUnrenderableItems(List<HomeSection> sections)
    {
        for (var sectionIndex = sections.Count - 1; sectionIndex >= 0; sectionIndex--)
        {
            var items = sections[sectionIndex].Items;
            for (var itemIndex = items.Count - 1; itemIndex >= 0; itemIndex--)
            {
                if (IsUnrenderable(items[itemIndex]))
                    items.RemoveAt(itemIndex);
            }

            if (items.Count == 0)
                sections.RemoveAt(sectionIndex);
        }
    }

    private static bool IsUnrenderable(HomeSectionItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.ImageUrl))
            return false;

        if (!string.IsNullOrWhiteSpace(item.Uri)
            && item.Uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
            return false;

        return item.ContentType is not HomeContentType.Episode
               || string.IsNullOrWhiteSpace(item.PublisherName);
    }

    private static void SetStringPreservingExisting(string? current, string? next, Action<string?> set)
    {
        if (string.IsNullOrWhiteSpace(next) && !string.IsNullOrWhiteSpace(current))
            return;

        if (current != next)
            set(next);
    }

    private static void SetMissingString(string? current, string? fallback, Action<string?> set)
    {
        if (string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(fallback))
            set(fallback);
    }
}
