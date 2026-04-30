using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
                if (current[i].ContentType != fresh[i].ContentType)
                {
                    Debug.WriteLine($"[shelf-recycle] ContentType drift uri={fresh[i].Uri} {current[i].ContentType}->{fresh[i].ContentType} (replace)");
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
                    if (current[i].ContentType != fresh[i].ContentType)
                    {
                        Debug.WriteLine($"[shelf-recycle] ContentType drift (post-move) uri={fresh[i].Uri} {current[i].ContentType}->{fresh[i].ContentType} (replace)");
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
        if (target.Title != source.Title) target.Title = source.Title;
        if (target.Subtitle != source.Subtitle) target.Subtitle = source.Subtitle;
        if (target.ImageUrl != source.ImageUrl) target.ImageUrl = source.ImageUrl;
        if (target.ColorHex != source.ColorHex) target.ColorHex = source.ColorHex;
        if (target.ContentType != source.ContentType) target.ContentType = source.ContentType;
        if (target.PlaceholderGlyph != source.PlaceholderGlyph) target.PlaceholderGlyph = source.PlaceholderGlyph;
        if (target.IsBaselineLoading != source.IsBaselineLoading) target.IsBaselineLoading = source.IsBaselineLoading;
        if (target.HasBaselinePreview != source.HasBaselinePreview) target.HasBaselinePreview = source.HasBaselinePreview;
        if (target.HeroImageUrl != source.HeroImageUrl) target.HeroImageUrl = source.HeroImageUrl;
        if (target.HeroColorHex != source.HeroColorHex) target.HeroColorHex = source.HeroColorHex;
        if (target.CanvasUrl != source.CanvasUrl) target.CanvasUrl = source.CanvasUrl;
        if (target.CanvasThumbnailUrl != source.CanvasThumbnailUrl) target.CanvasThumbnailUrl = source.CanvasThumbnailUrl;
        if (target.AudioPreviewUrl != source.AudioPreviewUrl) target.AudioPreviewUrl = source.AudioPreviewUrl;
        if (target.BaselineGroupTitle != source.BaselineGroupTitle) target.BaselineGroupTitle = source.BaselineGroupTitle;
        if (!ReferenceEquals(target.PreviewTracks, source.PreviewTracks)) target.PreviewTracks = source.PreviewTracks;
    }
}
