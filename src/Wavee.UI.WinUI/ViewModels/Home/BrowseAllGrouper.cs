using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Buckets the flat Browse All response into TOP / FOR YOU / GENRES /
/// MOOD &amp; ACTIVITY / CHARTS / MORE. Classification is by stable Spotify URI
/// (see <see cref="BrowseAllTaxonomy"/>) so the surface works in any locale —
/// the API translates labels but URIs are global. Eyebrow titles come from the
/// existing ResourceLoader pipeline (see <see cref="AppLocalization"/>) and
/// within-group sorting is locale-aware.
/// </summary>
internal static class BrowseAllGrouper
{
    private static readonly BrowseAllGroupKind[] GroupOrder =
    {
        BrowseAllGroupKind.Top,
        BrowseAllGroupKind.ForYou,
        BrowseAllGroupKind.Genres,
        BrowseAllGroupKind.MoodAndActivity,
        BrowseAllGroupKind.Charts,
        BrowseAllGroupKind.More,
    };

    public static IList<BrowseAllGroup> Group(IEnumerable<BrowseAllItem> items)
    {
        var bucket = new Dictionary<BrowseAllGroupKind, List<BrowseAllItem>>();
        foreach (var item in items)
        {
            var kind = BrowseAllTaxonomy.Classify(item.Uri);
            if (!bucket.TryGetValue(kind, out var list))
            {
                list = new List<BrowseAllItem>();
                bucket[kind] = list;
            }
            list.Add(item);
        }

        var result = new List<BrowseAllGroup>();
        foreach (var kind in GroupOrder)
        {
            if (!bucket.TryGetValue(kind, out var list) || list.Count == 0)
                continue;

            SortItems(list, kind);

            var group = new BrowseAllGroup
            {
                Eyebrow = AppLocalization.GetString(kind.ResourceKey()),
            };
            foreach (var item in list) group.Items.Add(item);
            result.Add(group);
        }
        return result;
    }

    public static IList<BrowseAllItem> Genres(IEnumerable<BrowseAllItem> items)
    {
        var list = items
            .Where(static item => BrowseAllTaxonomy.Classify(item.Uri) == BrowseAllGroupKind.Genres)
            .ToList();
        SortItems(list, BrowseAllGroupKind.Genres);
        return list;
    }

    private static void SortItems(List<BrowseAllItem> list, BrowseAllGroupKind kind)
    {
        if (kind == BrowseAllGroupKind.Top)
        {
            list.Sort((a, b) => Array.IndexOf(BrowseAllTaxonomy.TopOrder, a.Uri)
                .CompareTo(Array.IndexOf(BrowseAllTaxonomy.TopOrder, b.Uri)));
            return;
        }

        var labelComparer = StringComparer.Create(
            CultureInfo.CurrentUICulture, ignoreCase: true);
        list.Sort((a, b) => labelComparer.Compare(a.Label, b.Label));
    }
}
