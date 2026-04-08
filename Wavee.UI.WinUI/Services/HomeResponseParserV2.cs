using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Parses the V2 home feed response. This is a hybrid format:
/// - Shorts/quick-access and generic sections use V1-style content wrappers
/// - Recents section uses ListResponseWrapper with nested entity/trait items
/// V2 is detected by the presence of ListResponseWrapper or HomeRecentlyPlayedSectionData.
/// </summary>
public sealed class HomeResponseParserV2 : IHomeResponseParser
{
    // V1 parser for sections that still use the old content format
    private static readonly HomeResponseParserV1 V1Fallback = new();

    public bool CanParse(HomeResponse response)
    {
        var sections = response.Data?.Home?.SectionContainer?.Sections?.Items;
        if (sections == null) return false;

        // Detect V2 by looking for ListResponseWrapper or HomeRecentlyPlayedSectionData
        foreach (var section in sections)
        {
            if (section.Data?.TypeName == "HomeRecentlyPlayedSectionData")
                return true;

            if (section.SectionItems?.Items == null) continue;
            foreach (var item in section.SectionItems.Items)
            {
                if (item.Entity != null) return true;
                if (item.Content?.TypeName == "ListResponseWrapper") return true;
            }
        }

        return false;
    }

    public HomeParseResult Parse(HomeResponse response)
    {
        var greeting = response.Data?.Home?.Greeting?.TransformedLabel;
        var sections = MapSections(response);
        var chips = V1Fallback.Parse(response).Chips; // chips format is the same

        return new HomeParseResult(greeting, sections, chips);
    }

    private static List<HomeSection> MapSections(HomeResponse response)
    {
        var sections = new List<HomeSection>();
        var apiSections = response.Data?.Home?.SectionContainer?.Sections?.Items;
        System.Diagnostics.Debug.WriteLine($"[V2Parser] apiSections={apiSections?.Count ?? -1}");
        if (apiSections == null) return sections;

        foreach (var entry in apiSections)
        {
            System.Diagnostics.Debug.WriteLine($"[V2Parser] Section: type={entry.Data?.TypeName}, items={entry.SectionItems?.Items?.Count ?? -1}");
            var sectionType = GetSectionType(entry.Data?.TypeName);
            var rawTitle = entry.Data?.Title?.TransformedLabel;
            var title = !string.IsNullOrWhiteSpace(rawTitle)
                ? rawTitle
                : sectionType switch
                {
                    HomeSectionType.Shorts => "Quick access",
                    HomeSectionType.RecentlyPlayed => "Recents",
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

            // Extract header entity (e.g. artist avatar for "More like X" sections)
            var headerEntity = entry.Data?.HeaderEntity;
            if (headerEntity != null && headerEntity.TypeName is "ArtistResponseWrapper")
            {
                var artistData = headerEntity.GetArtistData();
                if (artistData != null)
                {
                    section.HeaderEntityName = artistData.Profile?.Name;
                    section.HeaderEntityUri = artistData.Uri;
                    section.HeaderEntityImageUrl = artistData.Visuals?.AvatarImage?.Sources?
                        .OrderByDescending(s => s.Width ?? 0)
                        .FirstOrDefault()?.Url;
                }
            }

            if (entry.SectionItems?.Items != null)
            {
                foreach (var itemEntry in entry.SectionItems.Items)
                {
                    System.Diagnostics.Debug.WriteLine($"[V2Parser]   Item: uri={itemEntry.Uri}, content={itemEntry.Content?.TypeName}, entity={itemEntry.Entity != null}, contentData={itemEntry.Content?.Data?.ValueKind}");
                    // Check if this is a ListResponseWrapper (Recents section)
                    if (itemEntry.Content?.TypeName == "ListResponseWrapper")
                    {
                        var listItems = UnwrapListItems(itemEntry.Content);
                        foreach (var li in listItems)
                            section.Items.Add(li);
                    }
                    else if (itemEntry.Entity != null)
                    {
                        // Direct entity item
                        var item = MapEntityItem(itemEntry.Entity, itemEntry.Uri, itemEntry.FormatListAttributes);
                        if (item != null) section.Items.Add(item);
                    }
                    else
                    {
                        // V1-style content item — delegate to V1 parser logic
                        var item = MapV1ContentItem(itemEntry);
                        if (item != null) section.Items.Add(item);
                    }
                }
            }

            if (section.Items.Count > 0)
                sections.Add(section);
        }

        return sections;
    }

    // ── ListResponseWrapper unwrapping ──

    private static List<HomeSectionItem> UnwrapListItems(HomeItemContent listContent)
    {
        var results = new List<HomeSectionItem>();
        if (listContent.Data is not { } rawData || rawData.ValueKind != JsonValueKind.Object)
        {
            System.Diagnostics.Debug.WriteLine($"[V2Parser] UnwrapListItems: Data is null or not object (kind={listContent.Data?.ValueKind})");
            return results;
        }

        // Navigate: data.items.items[] — each has "entity" + "formatListAttributes"
        if (!rawData.TryGetProperty("items", out var itemsWrapper))
        {
            System.Diagnostics.Debug.WriteLine($"[V2Parser] UnwrapListItems: no 'items' property. Keys: {string.Join(", ", rawData.EnumerateObject().Select(p => p.Name))}");
            return results;
        }
        if (!itemsWrapper.TryGetProperty("items", out var itemsArray))
        {
            System.Diagnostics.Debug.WriteLine($"[V2Parser] UnwrapListItems: no 'items.items'. Wrapper kind={itemsWrapper.ValueKind}");
            return results;
        }
        if (itemsArray.ValueKind != JsonValueKind.Array) return results;
        System.Diagnostics.Debug.WriteLine($"[V2Parser] UnwrapListItems: found {itemsArray.GetArrayLength()} nested items");

        foreach (var listItem in itemsArray.EnumerateArray())
        {
            // Parse entity
            HomeEntityWrapper? entity = null;
            if (listItem.TryGetProperty("entity", out var entityEl))
                entity = JsonSerializer.Deserialize(entityEl, HomeJsonContext.Default.HomeEntityWrapper);

            // Parse formatListAttributes
            List<HomeFormatListAttribute>? attrs = null;
            if (listItem.TryGetProperty("formatListAttributes", out var attrsEl)
                && attrsEl.ValueKind == JsonValueKind.Array)
            {
                attrs = new List<HomeFormatListAttribute>();
                foreach (var a in attrsEl.EnumerateArray())
                {
                    var key = a.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var val = a.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (key != null)
                        attrs.Add(new HomeFormatListAttribute { Key = key, Value = val });
                }
            }

            if (entity != null)
            {
                var item = MapEntityItem(entity, entity.Uri, attrs);
                if (item != null) results.Add(item);
            }
        }

        return results;
    }

    // ── Entity item mapping ──

    private static HomeSectionItem? MapEntityItem(
        HomeEntityWrapper entity,
        string? fallbackUri,
        List<HomeFormatListAttribute>? formatAttributes)
    {
        var entityData = entity.Data;
        if (entityData == null) return null;

        var entityUri = entity.Uri ?? entityData.Uri ?? fallbackUri;

        // Liked Songs / collection
        if (entityUri != null && entityUri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
            return MapLikedSongs(entityUri, entityData, formatAttributes);

        var contentType = ResolveContentType(entityData);
        var title = entityData.IdentityTrait?.Name;
        var imageUrl = ExtractImageUrl(entityData.VisualIdentityTrait?.SquareCoverImage);
        var subtitle = BuildSubtitle(entityData, contentType, formatAttributes);

        return new HomeSectionItem
        {
            Uri = entityUri,
            Title = title,
            Subtitle = subtitle,
            ImageUrl = imageUrl,
            ContentType = contentType
        };
    }

    // ── V1 content fallback ──

    private static HomeSectionItem? MapV1ContentItem(HomeSectionItemEntry entry)
    {
        var content = entry.Content;
        if (content == null) return null;

        return content.TypeName switch
        {
            "AlbumResponseWrapper" => MapV1Album(entry.Uri, content),
            "ArtistResponseWrapper" => MapV1Artist(entry.Uri, content),
            "PlaylistResponseWrapper" => MapV1Playlist(entry.Uri, content),
            "PodcastOrAudiobookResponseWrapper" => MapV1Podcast(entry.Uri, content),
            "UnknownType" => MapUnknownType(entry.Uri),
            _ => MapV1FromRawJson(entry.Uri, content)
        };
    }

    private static HomeSectionItem? MapV1Album(string? uri, HomeItemContent content)
    {
        var data = content.GetAlbumData();
        if (data == null) return null;
        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = data.Artists?.Items?.FirstOrDefault()?.Profile?.Name ?? "Album",
            ImageUrl = data.CoverArt?.Sources?.OrderByDescending(s => s.Width ?? 0).FirstOrDefault()?.Url,
            ContentType = HomeContentType.Album,
            ColorHex = data.CoverArt?.ExtractedColors?.ColorDark?.Hex
        };
    }

    private static HomeSectionItem? MapV1Artist(string? uri, HomeItemContent content)
    {
        var data = content.GetArtistData();
        if (data == null) return null;
        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Profile?.Name,
            Subtitle = "Artist",
            ImageUrl = data.Visuals?.AvatarImage?.Sources?.OrderByDescending(s => s.Width ?? 0).FirstOrDefault()?.Url,
            ContentType = HomeContentType.Artist,
            ColorHex = data.Visuals?.AvatarImage?.ExtractedColors?.ColorDark?.Hex
        };
    }

    private static HomeSectionItem? MapV1Playlist(string? uri, HomeItemContent content)
    {
        var data = content.GetPlaylistData();
        if (data == null) return null;
        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = Helpers.SpotifyHtmlHelper.StripHtml(data.Description) is { Length: > 0 } desc
                ? desc : data.OwnerV2?.Data?.Name,
            ImageUrl = data.Images?.Items?.FirstOrDefault()?.Sources?.OrderByDescending(s => s.Width ?? 0).FirstOrDefault()?.Url,
            ContentType = HomeContentType.Playlist,
            ColorHex = data.Images?.Items?.FirstOrDefault()?.ExtractedColors?.ColorDark?.Hex
        };
    }

    private static HomeSectionItem? MapV1Podcast(string? uri, HomeItemContent content)
    {
        var data = content.GetPodcastData();
        if (data == null) return null;
        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = data.Publisher?.Name,
            ImageUrl = data.CoverArt?.Sources?.OrderByDescending(s => s.Width ?? 0).FirstOrDefault()?.Url,
            ContentType = HomeContentType.Podcast
        };
    }

    private static HomeSectionItem? MapV1FromRawJson(string? uri, HomeItemContent content)
    {
        if (content.Data is not { } el || el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("__typename", out var tn) && tn.GetString() == "NotFound") return null;

        var item = new HomeSectionItem { Uri = uri, ContentType = InferContentType(uri) };
        if (el.TryGetProperty("name", out var name)) item.Title = name.GetString();
        if (item.Uri == null && el.TryGetProperty("uri", out var u)) item.Uri = u.GetString();
        return item.Title != null ? item : null;
    }

    private static HomeSectionItem? MapUnknownType(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
        {
            return new HomeSectionItem
            {
                Uri = uri,
                Title = "Liked Songs",
                ContentType = HomeContentType.Playlist,
                PlaceholderGlyph = "\uEB52",
                ColorHex = "#4B2A8A"
            };
        }
        return null;
    }

    // ── Liked Songs ──

    private static HomeSectionItem MapLikedSongs(string uri, HomeEntityData entityData,
        List<HomeFormatListAttribute>? formatAttributes)
    {
        var imageUrl = ExtractImageUrl(entityData.VisualIdentityTrait?.SquareCoverImage);
        var subtitle = "Playlist";
        if (formatAttributes?.Any(a => a.Key == "recent_type_saved") == true)
            subtitle = "Songs added";

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
        var entityType = entityData.EntityTypeTrait?.Type;
        if (!string.IsNullOrEmpty(entityType))
        {
            var ct = entityType switch
            {
                "ENTITY_TYPE_ALBUM" => HomeContentType.Album,
                "ENTITY_TYPE_ARTIST" => HomeContentType.Artist,
                "ENTITY_TYPE_PLAYLIST" => HomeContentType.Playlist,
                "ENTITY_TYPE_SHOW" => HomeContentType.Podcast,
                "ENTITY_TYPE_EPISODE" => HomeContentType.Episode,
                _ => HomeContentType.Unknown
            };
            if (ct != HomeContentType.Unknown) return ct;
        }

        var typedName = entityData.TypedEntity?.TypeName;
        if (!string.IsNullOrEmpty(typedName))
        {
            var ct = typedName switch
            {
                "AlbumResponseWrapper" => HomeContentType.Album,
                "ArtistResponseWrapper" => HomeContentType.Artist,
                "PlaylistResponseWrapper" => HomeContentType.Playlist,
                _ => HomeContentType.Unknown
            };
            if (ct != HomeContentType.Unknown) return ct;
        }

        return InferContentType(entityData.Uri);
    }

    // ── Image extraction ──

    private static string? ExtractImageUrl(HomeSquareCoverImage? coverImage)
    {
        if (coverImage == null) return null;

        if (coverImage.OriginalInstances is { Count: > 0 } instances)
        {
            var preferred = instances.FirstOrDefault(i =>
                i.Size is "IMAGE_SIZE_DEFAULT" or "IMAGE_SIZE_LARGE");
            var cdnUrl = (preferred ?? instances[0]).FlatFile?.CdnUrl;
            if (!string.IsNullOrEmpty(cdnUrl)) return cdnUrl;
        }

        if (coverImage.Image?.Data?.Sources is { Count: > 0 } sources)
            return sources.OrderByDescending(s => s.MaxWidth ?? 0).FirstOrDefault()?.Url;

        return null;
    }

    // ── Subtitle building ──

    private static string? BuildSubtitle(HomeEntityData entityData, HomeContentType contentType,
        List<HomeFormatListAttribute>? formatAttributes)
    {
        if (formatAttributes?.Any(a => a.Key == "recent_type_saved") == true)
        {
            if (contentType == HomeContentType.Album) return "Album added";
            var contributors = GetContributorNames(entityData);
            return !string.IsNullOrEmpty(contributors) ? contributors : "Added recently";
        }

        var names = GetContributorNames(entityData);
        if (!string.IsNullOrEmpty(names)) return names;

        var identityType = entityData.IdentityTrait?.Type;
        if (!string.IsNullOrEmpty(identityType)) return identityType;

        return contentType switch
        {
            HomeContentType.Artist => "Artist",
            HomeContentType.Album => "Album",
            HomeContentType.Playlist => "Playlist",
            _ => null
        };
    }

    private static string? GetContributorNames(HomeEntityData entityData)
    {
        var items = entityData.IdentityTrait?.Contributors?.Items;
        if (items == null || items.Count == 0) return null;
        var names = items.Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => c.Name!).ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
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
        return HomeContentType.Unknown;
    }
}
