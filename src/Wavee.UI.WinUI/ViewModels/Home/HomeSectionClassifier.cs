using System;
using System.Collections.Generic;
using System.Linq;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Buckets a <see cref="HomeSection"/> into one of the 4 region kinds the
/// redesigned home page renders. Returns <see langword="null"/> for sections
/// that should not appear in any region (Shorts go to the side rail; that's
/// already split out in <c>HomeViewModel.MapSectionsFromResponse</c>, but the
/// classifier still defends against a Shorts section sneaking back in).
/// </summary>
/// <remarks>
/// Real Spotify response (sample at C:\Users\ChristosKarapasias\Documents\home.txt,
/// 21 sections) lacks any explicit <c>sectionType</c> / facet field, so we infer
/// from <see cref="HomeSection.SectionType"/> + content typing + title patterns.
/// </remarks>
internal static class HomeSectionClassifier
{
    private static readonly HashSet<string> MadeForYouTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Your top mixes",
        "Jump back in",
        "Recommended Stations",
        "Recommended for today"
    };

    public static HomeRegionKind? ClassifyRegion(HomeSection section)
    {
        if (section is null || section.Items.Count == 0)
            return null;

        // Shorts is routed to the side rail upstream — defensive null here.
        if (section.SectionType == HomeSectionType.Shorts)
            return null;

        // Recently played has its own typename and is always its own region.
        if (section.SectionType == HomeSectionType.RecentlyPlayed)
            return HomeRegionKind.Recents;

        // Local files sections — built client-side by HomeViewModel with
        // synthetic SectionUri prefixes. Per-kind URIs fan out to dedicated
        // region bands so the Home page shows separate "Local TV / movies /
        // music / music videos" shelves instead of one mixed lump.
        // Generic "wavee:local:" prefix still routes to the legacy
        // catch-all band for back-compat.
        if (!string.IsNullOrEmpty(section.SectionUri))
        {
            if (section.SectionUri.StartsWith("wavee:local:shows", StringComparison.Ordinal))
                return HomeRegionKind.LocalShows;
            if (section.SectionUri.StartsWith("wavee:local:movies", StringComparison.Ordinal))
                return HomeRegionKind.LocalMovies;
            if (section.SectionUri.StartsWith("wavee:local:music_videos", StringComparison.Ordinal))
                return HomeRegionKind.LocalMusicVideos;
            if (section.SectionUri.StartsWith("wavee:local:music", StringComparison.Ordinal))
                return HomeRegionKind.LocalMusic;
            if (section.SectionUri.StartsWith("wavee:local:", StringComparison.Ordinal))
                return HomeRegionKind.LocalFiles;
        }

        // Podcast/episode sections — every item is podcast or episode content.
        // Honour the upstream IsPodcastSection flag too (it's set when the parser
        // detects all-podcast items + uses a fixed purple accent).
        if (section.IsPodcastSection)
            return HomeRegionKind.Podcasts;

        var allPodcast = section.Items.All(i =>
            i.ContentType is HomeContentType.Podcast or HomeContentType.Episode);
        if (allPodcast)
            return HomeRegionKind.Podcasts;

        // Made-for-you: title prefix or fixed-title set.
        // (The richer "any item carries attributes.madeFor.username" signal is
        //  not parsed yet; the title heuristic covers every Made-For variant in
        //  the real response sample. Extend if needed by capturing
        //  attributes.madeFor.username in HomeResponseParserV1/V2.MapPlaylist.)
        var title = section.Title;
        if (!string.IsNullOrEmpty(title))
        {
            if (title.StartsWith("Made For ", StringComparison.OrdinalIgnoreCase))
                return HomeRegionKind.MadeForYou;
            if (MadeForYouTitles.Contains(title))
                return HomeRegionKind.MadeForYou;
        }

        return HomeRegionKind.Discover;
    }
}
