using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Parses the V2 (entity/trait-based) home feed response format.
/// Each section item has an <see cref="HomeSectionItemEntry.Entity"/> with identity, visual identity,
/// and entity type traits instead of the V1 typed content wrappers.
/// </summary>
public sealed class HomeResponseParserV2 : IHomeResponseParser
{
    public bool CanParse(HomeResponse response)
    {
        var firstItem = response.Data?.Home?.SectionContainer?.Sections?.Items?
            .FirstOrDefault()?.SectionItems?.Items?.FirstOrDefault();

        return firstItem?.Entity != null;
    }

    public HomeParseResult Parse(HomeResponse response)
    {
        var greeting = response.Data?.Home?.Greeting?.TransformedLabel;
        var sections = MapSections(response);
        var chips = MapChips(response);

        return new HomeParseResult(greeting, sections, chips);
    }

    // ── Section mapping ──

    private static List<HomeSection> MapSections(HomeResponse response)
    {
        var sections = new List<HomeSection>();
        var apiSections = response.Data?.Home?.SectionContainer?.Sections?.Items;
        if (apiSections == null) return sections;

        foreach (var entry in apiSections)
        {
            var sectionType = GetSectionType(entry.Data?.TypeName);

            var rawTitle = entry.Data?.Title?.TransformedLabel;
            var title = !string.IsNullOrWhiteSpace(rawTitle)
                ? rawTitle
                : sectionType switch
                {
                    HomeSectionType.Shorts => "Quick access",
                    HomeSectionType.RecentlyPlayed => "Recently played",
                    HomeSectionType.Baseline => entry.Data?.TypeName ?? "Recommended",
                    _ => "Untitled section"
                };

            var section = new HomeSection
            {
                Title = title,
                Subtitle = entry.Data?.Subtitle?.TransformedLabel,
                SectionType = sectionType,
                SectionUri = entry.Uri ?? ""
            };

            if (entry.SectionItems?.Items != null)
            {
                foreach (var itemEntry in entry.SectionItems.Items)
                {
                    var item = MapSectionItem(itemEntry);
                    if (item != null)
                        section.Items.Add(item);
                }
            }

            if (section.Items.Count > 0)
                sections.Add(section);
        }

        return sections;
    }

    private static HomeSectionItem? MapSectionItem(HomeSectionItemEntry entry)
    {
        var entity = entry.Entity;
        if (entity == null) return null;

        var entityData = entity.Data;
        if (entityData == null) return null;

        var entityUri = entity.Uri ?? entityData.Uri ?? entry.Uri;

        // Liked Songs / collection special case
        if (entityUri != null && entityUri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
        {
            return MapLikedSongs(entityUri, entityData, entry);
        }

        var contentType = ResolveContentType(entityData);
        var title = entityData.IdentityTrait?.Name;
        var imageUrl = ExtractImageUrl(entityData.VisualIdentityTrait?.SquareCoverImage);
        var subtitle = BuildSubtitle(entityData, contentType, entry.FormatListAttributes);

        return new HomeSectionItem
        {
            Uri = entityUri,
            Title = title,
            Subtitle = subtitle,
            ImageUrl = imageUrl,
            ContentType = contentType
        };
    }

    // ── Liked Songs ──

    private static HomeSectionItem MapLikedSongs(string uri, HomeEntityData entityData, HomeSectionItemEntry entry)
    {
        var imageUrl = ExtractImageUrl(entityData.VisualIdentityTrait?.SquareCoverImage);

        // Determine subtitle based on FormatListAttributes
        var subtitle = "Playlist";
        if (entry.FormatListAttributes != null)
        {
            var hasSaved = entry.FormatListAttributes.Any(a => a.Key == "recent_type_saved");
            if (hasSaved)
                subtitle = "Songs added";
        }

        return new HomeSectionItem
        {
            Uri = uri,
            Title = entityData.IdentityTrait?.Name ?? "Liked Songs",
            Subtitle = subtitle,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Playlist,
            PlaceholderGlyph = "\uEB52",
            ColorHex = "#4B2A8A"
        };
    }

    // ── Content type resolution ──

    private static HomeContentType ResolveContentType(HomeEntityData entityData)
    {
        // Primary: EntityTypeTrait.Type
        var entityType = entityData.EntityTypeTrait?.Type;
        if (!string.IsNullOrEmpty(entityType))
        {
            var ct = MapEntityTypeString(entityType);
            if (ct != HomeContentType.Unknown)
                return ct;
        }

        // Fallback: TypedEntity.__typename (same wrapper names as V1)
        var typedEntityName = entityData.TypedEntity?.TypeName;
        if (!string.IsNullOrEmpty(typedEntityName))
        {
            var ct = MapTypedEntityName(typedEntityName);
            if (ct != HomeContentType.Unknown)
                return ct;
        }

        // Last resort: infer from URI
        var uri = entityData.Uri;
        return InferContentType(uri);
    }

    private static HomeContentType MapEntityTypeString(string entityType) => entityType switch
    {
        "ENTITY_TYPE_ALBUM" => HomeContentType.Album,
        "ENTITY_TYPE_ARTIST" => HomeContentType.Artist,
        "ENTITY_TYPE_PLAYLIST" => HomeContentType.Playlist,
        "ENTITY_TYPE_SHOW" => HomeContentType.Podcast,
        "ENTITY_TYPE_EPISODE" => HomeContentType.Episode,
        _ => HomeContentType.Unknown
    };

    private static HomeContentType MapTypedEntityName(string typeName) => typeName switch
    {
        "AlbumResponseWrapper" => HomeContentType.Album,
        "ArtistResponseWrapper" => HomeContentType.Artist,
        "PlaylistResponseWrapper" => HomeContentType.Playlist,
        "PodcastOrAudiobookResponseWrapper" => HomeContentType.Podcast,
        _ => HomeContentType.Unknown
    };

    // ── Image extraction ──

    private static string? ExtractImageUrl(HomeSquareCoverImage? coverImage)
    {
        if (coverImage == null) return null;

        // Prefer OriginalInstances with known sizes
        if (coverImage.OriginalInstances is { Count: > 0 } instances)
        {
            // Prefer IMAGE_SIZE_DEFAULT or IMAGE_SIZE_LARGE
            var preferred = instances.FirstOrDefault(i =>
                i.Size is "IMAGE_SIZE_DEFAULT" or "IMAGE_SIZE_LARGE");

            var cdnUrl = (preferred ?? instances[0]).FlatFile?.CdnUrl;
            if (!string.IsNullOrEmpty(cdnUrl))
                return cdnUrl;
        }

        // Fall back to Image.Data.Sources sorted by maxWidth desc
        if (coverImage.Image?.Data?.Sources is { Count: > 0 } sources)
        {
            return sources
                .OrderByDescending(s => s.MaxWidth ?? 0)
                .FirstOrDefault()?.Url;
        }

        return null;
    }

    // ── Subtitle building ──

    private static string? BuildSubtitle(
        HomeEntityData entityData,
        HomeContentType contentType,
        List<HomeFormatListAttribute>? formatAttributes)
    {
        // Check FormatListAttributes for recently-saved items
        if (formatAttributes != null)
        {
            var hasSaved = formatAttributes.Any(a => a.Key == "recent_type_saved");
            if (hasSaved)
            {
                // For albums that were recently saved, indicate that
                if (contentType == HomeContentType.Album)
                    return "Album added";

                // For other saved types, check if we have contributor info to supplement
                var contributors = GetContributorNames(entityData);
                return !string.IsNullOrEmpty(contributors) ? contributors : "Added recently";
            }
        }

        // Contributors (artists, etc.)
        var contributorNames = GetContributorNames(entityData);
        if (!string.IsNullOrEmpty(contributorNames))
            return contributorNames;

        // IdentityTrait.Type (e.g. "Album", "Single", "Artist")
        var identityType = entityData.IdentityTrait?.Type;
        if (!string.IsNullOrEmpty(identityType))
            return identityType;

        // Fallback based on content type
        return contentType switch
        {
            HomeContentType.Artist => "Artist",
            HomeContentType.Album => "Album",
            HomeContentType.Playlist => "Playlist",
            HomeContentType.Podcast => "Podcast",
            _ => null
        };
    }

    private static string? GetContributorNames(HomeEntityData entityData)
    {
        var contributors = entityData.IdentityTrait?.Contributors?.Items;
        if (contributors == null || contributors.Count == 0) return null;

        var names = contributors
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => c.Name!)
            .ToList();

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    // ── Chips ──

    private static List<HomeChipViewModel> MapChips(HomeResponse response)
    {
        var chips = new List<HomeChipViewModel>();
        var apiChips = response.Data?.Home?.HomeChips;
        if (apiChips == null) return chips;

        foreach (var chip in apiChips)
        {
            var vm = new HomeChipViewModel
            {
                Id = chip.Id ?? "",
                Label = chip.Label?.TransformedLabel ?? ""
            };

            if (chip.SubChips != null)
            {
                foreach (var sub in chip.SubChips)
                {
                    vm.SubChips.Add(new HomeChipViewModel
                    {
                        Id = sub.Id ?? "",
                        Label = sub.Label?.TransformedLabel ?? ""
                    });
                }
            }

            chips.Add(vm);
        }

        return chips;
    }

    // ── Helpers ──

    private static HomeSectionType GetSectionType(string? typeName) => typeName switch
    {
        "HomeShortsSectionData" => HomeSectionType.Shorts,
        "HomeRecentlyPlayedSectionData" => HomeSectionType.RecentlyPlayed,
        "HomeFeedBaselineSectionData" => HomeSectionType.Baseline,
        _ => HomeSectionType.Generic
    };

    private static HomeContentType InferContentType(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return HomeContentType.Unknown;
        if (uri.Contains(":playlist:", StringComparison.Ordinal)) return HomeContentType.Playlist;
        if (uri.Contains(":album:", StringComparison.Ordinal)) return HomeContentType.Album;
        if (uri.Contains(":artist:", StringComparison.Ordinal)) return HomeContentType.Artist;
        if (uri.Contains(":show:", StringComparison.Ordinal)) return HomeContentType.Podcast;
        if (uri.Contains(":episode:", StringComparison.Ordinal)) return HomeContentType.Episode;
        return HomeContentType.Unknown;
    }
}
