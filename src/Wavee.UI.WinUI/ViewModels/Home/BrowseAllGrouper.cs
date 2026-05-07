using System;
using System.Collections.Generic;
using System.Linq;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Heuristic taxonomy for the Browse All surface. The Pathfinder
/// <c>browseAll</c> response gives a flat list (~68 items) with no built-in
/// grouping; we cluster by exact-match label into TOP / FOR YOU / GENRES /
/// MOOD &amp; ACTIVITY / CHARTS, with anything unmatched falling into MORE.
/// Matches the prototype design.
/// </summary>
internal static class BrowseAllGrouper
{
    private static readonly Dictionary<string, string[]> GroupMembers = new(StringComparer.Ordinal)
    {
        ["TOP"] = new[] { "Music", "Podcasts", "Audiobooks", "Live Events" },
        ["FOR YOU"] = new[] { "Made For You", "Discover", "Fresh Finds", "RADAR", "EQUAL", "New Releases", "Trending" },
        ["GENRES"] = new[] {
            "Pop", "Hip-Hop", "Rock", "Dance/Electronic", "Indie", "R&B", "Soul",
            "Country", "Classical", "Jazz", "Metal", "Punk", "Alternative",
            "Folk & Acoustic", "Funk & Disco", "Reggae", "Blues", "Ambient",
            "K-pop", "Latin", "Afro", "Dutch music", "Arab", "Caribbean"
        },
        ["MOOD & ACTIVITY"] = new[] {
            "Mood", "Chill", "Party", "Sleep", "Love", "Focus", "Workout Music",
            "Fitness", "In the car", "At Home", "Travel", "Cooking & Dining",
            "Wellness", "Songwriters", "Nature & Noise"
        },
        ["CHARTS"] = new[] { "Charts", "Podcast Charts" }
    };

    private static readonly string[] GroupOrder =
    {
        "TOP", "FOR YOU", "GENRES", "MOOD & ACTIVITY", "CHARTS", "MORE"
    };

    public static IList<BrowseAllGroup> Group(IEnumerable<BrowseAllItem> items)
    {
        var bucket = new Dictionary<string, List<BrowseAllItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = "MORE";
            foreach (var (groupKey, members) in GroupMembers)
            {
                if (members.Contains(item.Label, StringComparer.OrdinalIgnoreCase))
                {
                    key = groupKey;
                    break;
                }
            }
            if (!bucket.TryGetValue(key, out var list))
            {
                list = new List<BrowseAllItem>();
                bucket[key] = list;
            }
            list.Add(item);
        }

        var result = new List<BrowseAllGroup>();
        foreach (var key in GroupOrder)
        {
            if (!bucket.TryGetValue(key, out var list) || list.Count == 0) continue;

            // TOP keeps its API order (Music → Podcasts → Audiobooks → Live Events
            // is semantically meaningful). Everything else: alphabetical for scan.
            if (key != "TOP")
            {
                list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // Within TOP, force the canonical ordering even if the API shuffles.
                var canonical = GroupMembers["TOP"];
                list.Sort((a, b) => Array.IndexOf(canonical, a.Label).CompareTo(Array.IndexOf(canonical, b.Label)));
            }

            var group = new BrowseAllGroup { Eyebrow = key };
            foreach (var item in list) group.Items.Add(item);
            result.Add(group);
        }
        return result;
    }
}
