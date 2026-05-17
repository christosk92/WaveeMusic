using System.Collections.Generic;
using System.Linq;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.ViewModels.Home;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Single-target CTA the Browse mapper extracts from a
/// <c>BrowseRelatedSectionData</c> section. Always rendered as a separate
/// button on the Browse page (not folded into the chip grid). Public so
/// <see cref="BrowseViewModel"/> can surface it as an <c>ObservableProperty</c>.
/// </summary>
public sealed record BrowseCta(string Label, string Uri);

/// <summary>
/// Result of <see cref="BrowseResponseMapper.MapSections"/>. The browse page
/// API returns three structurally different section kinds and we route each
/// to the surface that fits it: editorial shelves render as horizontal
/// ShelfScrollers, BrowseGridSectionData items become small text chips
/// grouped via <see cref="BrowseAllGrouper"/>, and BrowseRelatedSectionData
/// becomes a single CTA button.
/// </summary>
internal readonly record struct BrowseMapResult(
    List<HomeSection> Editorial,
    IList<BrowseAllGroup> BrowseGroups,
    BrowseCta? Cta);

/// <summary>
/// Maps Pathfinder <c>browsePage</c> response sections / items into the
/// shared <see cref="HomeSection"/> + <see cref="HomeSectionItem"/> model
/// so <see cref="Views.BrowsePage"/> can render shelves with the same
/// templates Home uses (<c>ContentCard</c>, <c>ShelfScroller</c>, etc.).
/// </summary>
internal static class BrowseResponseMapper
{
    private const string GridSectionTypeName = "BrowseGridSectionData";
    private const string RelatedSectionTypeName = "BrowseRelatedSectionData";

    public static BrowseMapResult MapSections(BrowsePageResponse? response)
    {
        var editorial = new List<HomeSection>();
        IList<BrowseAllGroup> browseGroups = new List<BrowseAllGroup>();
        BrowseCta? cta = null;

        var apiSections = response?.Data?.Browse?.Sections?.Items;
        if (apiSections == null) return new BrowseMapResult(editorial, browseGroups, cta);

        foreach (var section in apiSections)
        {
            var entries = section.SectionItems?.Items;
            if (entries == null) continue;

            // Discriminate at the SECTION level on __typename. The browse page
            // mixes editorial shelves (BrowseSectionData / similar) with two
            // categorical kinds — the grid of category tiles and the related-
            // links CTA — that need totally different rendering surfaces.
            var sectionTypeName = section.Data?.TypeName;

            if (sectionTypeName == GridSectionTypeName)
            {
                // Project each grid entry through MapBrowseSubpage to get the
                // label + colorHex + uri, then convert to BrowseAllItem so the
                // existing BrowseAllGrouper can bucket them under TOP / FOR YOU
                // / GENRES / MOOD & ACTIVITY / CHARTS / MORE — same layout the
                // destination "Discover something new" page uses.
                var gridItems = new List<BrowseAllItem>();
                foreach (var entry in entries)
                {
                    var mapped = MapBrowseSubpage(entry, entry.Content?.Data);
                    if (mapped is null || string.IsNullOrEmpty(mapped.Title)) continue;
                    gridItems.Add(new BrowseAllItem
                    {
                        Label = mapped.Title!,
                        AccentHex = mapped.ColorHex ?? "",
                        Uri = mapped.Uri ?? entry.Uri ?? ""
                    });
                }
                if (gridItems.Count > 0)
                    browseGroups = BrowseAllGrouper.Group(gridItems);
                continue;
            }

            if (sectionTypeName == RelatedSectionTypeName)
            {
                // BrowseRelatedSectionData typically holds a single CTA card
                // ("Explore all categories"). Take the first valid entry.
                foreach (var entry in entries)
                {
                    var mapped = MapBrowseSubpage(entry, entry.Content?.Data);
                    if (mapped is null || string.IsNullOrEmpty(mapped.Title)) continue;
                    cta = new BrowseCta(mapped.Title!, mapped.Uri ?? entry.Uri ?? "");
                    break;
                }
                continue;
            }

            // Default — editorial shelf.
            var shelf = new HomeSection
            {
                Title = section.Data?.Title?.TransformedLabel,
                Subtitle = section.Data?.Subtitle?.TransformedLabel,
                SectionType = HomeSectionType.Generic,
                SectionUri = section.Uri ?? ""
            };

            foreach (var entry in entries)
            {
                var item = MapItem(entry);
                if (item != null)
                    shelf.Items.Add(item);
            }

            if (shelf.Items.Count > 0)
                editorial.Add(shelf);
        }

        return new BrowseMapResult(editorial, browseGroups, cta);
    }

    private static HomeSectionItem? MapItem(PathfinderBrowseSectionItem entry)
    {
        var content = entry.Content;
        if (content?.Data is null) return null;

        return content.TypeName switch
        {
            "PlaylistResponseWrapper" => MapPlaylist(entry, content.Data),
            "AlbumResponseWrapper" => MapAlbum(entry, content.Data),
            "PodcastOrAudiobookResponseWrapper" => MapPodcast(entry, content.Data),
            "BrowseSectionContainerWrapper" => MapBrowseSubpage(entry, content.Data),
            _ => null
        };
    }

    private static HomeSectionItem MapPlaylist(PathfinderBrowseSectionItem entry, PathfinderBrowseContentData data)
    {
        var (small, medium, large) = SpotifyImageHelper.BucketSources(
            data.Images?.Items?.FirstOrDefault()?.Sources,
            s => s.Url, s => s.Width);

        // Description can carry HTML (<a href="spotify:..."> etc.) — strip for
        // the card subtitle, fall back to the owner name when there's nothing.
        var stripped = SpotifyHtmlHelper.StripHtml(data.Description);
        var subtitle = !string.IsNullOrEmpty(stripped) ? stripped : data.OwnerV2?.Data?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? entry.Uri,
            Title = data.Name,
            Subtitle = subtitle,
            ImageUrl = large ?? medium ?? small,
            ImageSmallUrl = small,
            ImageMediumUrl = medium,
            ImageLargeUrl = large,
            ContentType = HomeContentType.Playlist
        };
    }

    private static HomeSectionItem MapAlbum(PathfinderBrowseSectionItem entry, PathfinderBrowseContentData data)
    {
        var (small, medium, large) = SpotifyImageHelper.BucketSources(
            data.CoverArt?.Sources, s => s.Url, s => s.Width);

        var artistName = data.Artists?.Items?.FirstOrDefault()?.Profile?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? entry.Uri,
            Title = data.Name,
            Subtitle = artistName ?? "Album",
            ImageUrl = large ?? medium ?? small,
            ImageSmallUrl = small,
            ImageMediumUrl = medium,
            ImageLargeUrl = large,
            ContentType = HomeContentType.Album
        };
    }

    private static HomeSectionItem MapPodcast(PathfinderBrowseSectionItem entry, PathfinderBrowseContentData data)
    {
        var (small, medium, large) = SpotifyImageHelper.BucketSources(
            data.CoverArt?.Sources, s => s.Url, s => s.Width);

        return new HomeSectionItem
        {
            Uri = data.Uri ?? entry.Uri,
            Title = data.Name,
            Subtitle = data.Publisher?.Name,
            ImageUrl = large ?? medium ?? small,
            ImageSmallUrl = small,
            ImageMediumUrl = medium,
            ImageLargeUrl = large,
            ContentType = HomeContentType.Podcast
        };
    }

    /// <summary>
    /// Sub-page tile — the "Browse all" grid inside a Browse page (Made For
    /// You / New Releases / genre tiles). Wraps the cardRepresentation so the
    /// tile renders with its own artwork + accent colour and click navigates
    /// to another <c>spotify:page:</c> destination.
    /// </summary>
    private static HomeSectionItem? MapBrowseSubpage(PathfinderBrowseSectionItem entry, PathfinderBrowseContentData? data)
    {
        var card = data?.Data?.CardRepresentation;
        if (card is null) return null;
        var label = card.Title?.TransformedLabel;
        if (string.IsNullOrEmpty(label)) return null;

        var (small, medium, large) = SpotifyImageHelper.BucketSources(
            card.Artwork?.Sources, s => s.Url, s => s.Width);

        return new HomeSectionItem
        {
            Uri = entry.Uri,
            Title = label,
            Subtitle = "Browse",
            ImageUrl = large ?? medium ?? small,
            ImageSmallUrl = small,
            ImageMediumUrl = medium,
            ImageLargeUrl = large,
            ContentType = HomeContentType.Unknown,
            ColorHex = card.BackgroundColor?.Hex
        };
    }
}
