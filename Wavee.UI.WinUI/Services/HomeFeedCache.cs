using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Snapshot of the home feed data.
/// </summary>
public sealed record HomeFeedSnapshot(string? Greeting, List<HomeSection> Sections);

/// <summary>
/// Singleton cache for the home feed. Extends <see cref="PageCache{TSnapshot}"/> with
/// home-specific diff logic for sections and items.
/// </summary>
public sealed class HomeFeedCache : PageCache<HomeFeedSnapshot>, IHomeFeedCache
{
    public HomeFeedCache(ILogger<HomeFeedCache>? logger = null) : base(logger)
    {
    }

    protected override async Task<HomeFeedSnapshot> FetchCoreAsync(ISession session, CancellationToken ct)
    {
        var response = await session.Pathfinder.GetHomeAsync(sectionItemsLimit: 10, ct);

        var greeting = response.Data?.Home?.Greeting?.TransformedLabel;
        var sections = HomeViewModel.MapSectionsFromResponse(response);

        Logger?.LogDebug("Home feed cached: {Count} sections", sections.Count);
        return new HomeFeedSnapshot(greeting, sections);
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
        string? newGreeting = null)
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
                DiffItems(current[i].Items, fresh[i].Items);
                // Update title/subtitle if changed
                if (current[i].Title != fresh[i].Title) current[i].Title = fresh[i].Title;
                if (current[i].Subtitle != fresh[i].Subtitle) current[i].Subtitle = fresh[i].Subtitle;
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
                    DiffItems(current[i].Items, fresh[i].Items);
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
                // Same item — update properties if changed
                UpdateItemInPlace(current[i], fresh[i]);
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
                    UpdateItemInPlace(current[i], fresh[i]);
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
    }
}
