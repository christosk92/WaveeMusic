using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Parses the V1 (content-based) home feed response format.
/// Each section item has a <see cref="HomeSectionItemEntry.Content"/> with a typed wrapper
/// (AlbumResponseWrapper, ArtistResponseWrapper, etc.) and a JsonElement data payload.
/// </summary>
public sealed class HomeResponseParserV1 : IHomeResponseParser
{
    public bool CanParse(HomeResponse response)
    {
        var firstItem = response.Data?.Home?.SectionContainer?.Sections?.Items?
            .FirstOrDefault()?.SectionItems?.Items?.FirstOrDefault();

        return firstItem?.Content != null;
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

        var rawSections = HomeRawJsonHelper.GetRawSectionJsonByIndex(response);
        var sectionIndex = -1;

        foreach (var entry in apiSections)
        {
            sectionIndex++;
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
                SectionUri = entry.Uri ?? "",
                RawSpotifyJson = sectionIndex < rawSections.Count ? rawSections[sectionIndex] : null
            };

            // Extract header entity (e.g. artist for "More like X" sections)
            var headerEntity = entry.Data?.HeaderEntity;
            if (headerEntity is { TypeName: "ArtistResponseWrapper" })
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
                    var item = MapSectionItem(itemEntry);
                    if (item != null)
                    {
                        if (sectionType == HomeSectionType.Baseline)
                            item.IsBaselineLoading = true;
                        section.Items.Add(item);
                    }
                }
            }

            if (section.Items.Count > 0)
            {
                // Podcast-only sections get their own visual identity — fixed
                // podcast-purple wash + a mic glyph in the header — so the
                // user clocks "this row is podcasts" before any card paints.
                // Take precedence over the per-cover extracted accent below.
                section.IsPodcastSection = section.Items.All(i =>
                    i.ContentType is HomeContentType.Episode or HomeContentType.Podcast);

                // Pull a visual-identity accent from the section's first item
                // that carries a Spotify-extracted dark color. Brushes are
                // built later by HomeViewModel when the section is added to
                // the bound Sections collection (theme dependency).
                section.AccentColorHex = section.IsPodcastSection
                    ? PodcastSectionAccentHex
                    : section.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.ColorHex))?.ColorHex;
                sections.Add(section);
            }
        }

        return sections;
    }

    /// <summary>
    /// Soft podcast-purple. Used as the accent override for podcast-only
    /// shelves so the section header wash reads as a distinct visual family
    /// from the album/playlist accents (which come from per-cover extraction).
    /// </summary>
    internal const string PodcastSectionAccentHex = "#9B5DE5";

    private static HomeSectionItem? MapSectionItem(HomeSectionItemEntry entry)
    {
        var content = entry.Content;
        if (content == null) return null;

        var result = content.TypeName switch
        {
            "ArtistResponseWrapper" => MapArtist(entry.Uri, content),
            "PlaylistResponseWrapper" => MapPlaylist(entry.Uri, content),
            "AlbumResponseWrapper" => MapAlbum(entry.Uri, content),
            "PodcastOrAudiobookResponseWrapper" => MapPodcast(entry.Uri, content),
            "EpisodeOrChapterResponseWrapper" => MapEpisode(entry.Uri, content),
            _ => (HomeSectionItem?)null
        };

        // If typed deserialization failed or returned incomplete data, try raw JsonElement extraction
        if (result == null || result.Title == null)
        {
            if (content.Data is { } el && el.ValueKind == JsonValueKind.Object)
            {
                // Skip items the API marks as "NotFound"
                if (el.TryGetProperty("__typename", out var tn)
                    && tn.GetString() == "NotFound")
                {
                    return null;
                }

                result ??= new HomeSectionItem { Uri = entry.Uri, ContentType = InferContentType(entry.Uri) };
                EnrichFromRawJson(result, el);
            }
        }

        return result ?? MapUnknownType(entry.Uri);
    }

    // ── Typed mappers ──

    private static HomeSectionItem? MapArtist(string? uri, HomeItemContent content)
    {
        var data = content.GetArtistData();
        if (data == null) return null;

        var imageUrl = data.Visuals?.AvatarImage?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.Visuals?.AvatarImage?.ExtractedColors?.ColorDark?.Hex;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Profile?.Name,
            Subtitle = "Artist",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Artist,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapPlaylist(string? uri, HomeItemContent content)
    {
        var data = content.GetPlaylistData();
        if (data == null) return null;

        var imageUrl = data.Images?.Items?.FirstOrDefault()?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.Images?.Items?.FirstOrDefault()?.ExtractedColors?.ColorDark?.Hex;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = SpotifyHtmlHelper.StripHtml(data.Description) is { Length: > 0 } desc
                ? desc
                : data.OwnerV2?.Data?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Playlist,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapAlbum(string? uri, HomeItemContent content)
    {
        var data = content.GetAlbumData();
        if (data == null) return null;

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.CoverArt?.ExtractedColors?.ColorDark?.Hex;
        var artistName = data.Artists?.Items?.FirstOrDefault()?.Profile?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = artistName ?? "Album",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Album,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapPodcast(string? uri, HomeItemContent content)
    {
        var data = content.GetPodcastData();
        if (data == null) return null;

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = data.Publisher?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Podcast,
            PublisherName = data.Publisher?.Name
        };
    }

    private static HomeSectionItem? MapEpisode(string? uri, HomeItemContent content)
    {
        var data = content.GetEpisodeData();
        if (data == null) return null;

        // Cover art priority: episode cover → show cover (episodes often
        // ship without their own art and inherit from the show).
        var imageUrl = data.CoverArt?.Sources?
                            .OrderByDescending(s => s.Width ?? 0)
                            .FirstOrDefault()?.Url
                       ?? data.PodcastV2?.Data?.CoverArt?.Sources?
                            .OrderByDescending(s => s.Width ?? 0)
                            .FirstOrDefault()?.Url;

        var publisherName = data.PodcastV2?.Data?.Name
                            ?? data.PodcastV2?.Data?.Publisher?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = publisherName,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Episode,
            DurationMs = data.Duration?.TotalMilliseconds,
            PlayedPositionMs = data.PlayedState?.PlayPositionMilliseconds,
            PlayedState = MapEpisodePlayedState(data.PlayedState?.State),
            PublisherName = publisherName,
            IsVideoPodcast = data.MediaTypes?.Any(m =>
                string.Equals(m, "VIDEO", StringComparison.OrdinalIgnoreCase)) == true,
            ReleaseDateIso = data.ReleaseDate?.IsoString
        };
    }

    /// <summary>
    /// Map Spotify's <c>playedState.state</c> string into the typed enum.
    /// Returns null when the state is unknown / missing — the card collapses
    /// to its NotStarted-ish layout (just "{duration}") in that case.
    /// </summary>
    internal static EpisodePlayedState? MapEpisodePlayedState(string? state) => state switch
    {
        "NOT_STARTED" => EpisodePlayedState.NotStarted,
        "IN_PROGRESS" => EpisodePlayedState.InProgress,
        "COMPLETED" => EpisodePlayedState.Completed,
        _ => null
    };

    // ── Raw JSON fallback ──

    private static void EnrichFromRawJson(HomeSectionItem item, JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            return;

        if (item.Title == null && raw.TryGetProperty("name", out var name))
            item.Title = name.GetString();

        if (item.Uri == null && raw.TryGetProperty("uri", out var uri))
            item.Uri = uri.GetString();

        if (item.ImageUrl == null)
            item.ImageUrl = ExtractImageUrlFromJson(raw);

        if (item.Subtitle == null && raw.TryGetProperty("description", out var desc))
        {
            var descStr = SpotifyHtmlHelper.StripHtml(desc.GetString());
            if (!string.IsNullOrEmpty(descStr))
                item.Subtitle = descStr;
        }

        if (item.ColorHex == null)
            item.ColorHex = ExtractColorFromJson(raw);

        // Try nested "data" wrapper (double-wrapped items)
        if (item.Title == null && raw.TryGetProperty("data", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            EnrichFromRawJson(item, nested);
        }
    }

    private static string? ExtractImageUrlFromJson(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            return null;

        // Playlist: images.items[0].sources
        if (raw.TryGetProperty("images", out var images)
            && images.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0)
        {
            var url = GetLargestSourceUrl(items[0]);
            if (url != null) return url;
        }

        // Album/Podcast: coverArt.sources
        if (raw.TryGetProperty("coverArt", out var coverArt))
        {
            var url = GetLargestSourceUrl(coverArt);
            if (url != null) return url;
        }

        // Artist: visuals.avatarImage.sources
        if (raw.TryGetProperty("visuals", out var visuals)
            && visuals.TryGetProperty("avatarImage", out var avatar))
        {
            var url = GetLargestSourceUrl(avatar);
            if (url != null) return url;
        }

        return null;
    }

    private static string? GetLargestSourceUrl(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
            return null;

        if (!container.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0)
            return null;

        string? bestUrl = null;
        int maxWidth = -1;
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
                continue;

            var width = source.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
                ? w.GetInt32() : 0;
            if (width > maxWidth || bestUrl == null)
            {
                maxWidth = width;
                bestUrl = source.TryGetProperty("url", out var url) ? url.GetString() : null;
            }
        }
        return bestUrl;
    }

    private static string? ExtractColorFromJson(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            return null;

        // images.items[0].extractedColors.colorDark.hex
        if (raw.TryGetProperty("images", out var images)
            && images.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0
            && items[0].ValueKind == JsonValueKind.Object
            && items[0].TryGetProperty("extractedColors", out var ec)
            && ec.ValueKind == JsonValueKind.Object
            && ec.TryGetProperty("colorDark", out var cd)
            && cd.ValueKind == JsonValueKind.Object
            && cd.TryGetProperty("hex", out var hex))
            return hex.GetString();

        // coverArt.extractedColors.colorDark.hex
        if (raw.TryGetProperty("coverArt", out var coverArt)
            && coverArt.ValueKind == JsonValueKind.Object
            && coverArt.TryGetProperty("extractedColors", out var ec2)
            && ec2.ValueKind == JsonValueKind.Object
            && ec2.TryGetProperty("colorDark", out var cd2)
            && cd2.ValueKind == JsonValueKind.Object
            && cd2.TryGetProperty("hex", out var hex2))
            return hex2.GetString();

        return null;
    }

    // ── Unknown / collection items ──

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

        var parts = uri.Split(':');
        if (parts.Length < 2) return null;

        var type = parts[1];
        return new HomeSectionItem
        {
            Uri = uri,
            Title = type switch
            {
                "artist" => "Artist",
                "album" => "Album",
                "playlist" => "Playlist",
                _ => null
            },
            ContentType = type switch
            {
                "artist" => HomeContentType.Artist,
                "album" => HomeContentType.Album,
                "playlist" => HomeContentType.Playlist,
                _ => HomeContentType.Unknown
            }
        };
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
