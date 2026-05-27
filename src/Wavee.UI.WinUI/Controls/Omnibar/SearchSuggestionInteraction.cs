using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.Omnibar;

internal static class SearchSuggestionInteraction
{
    public static IDragPayload? BuildDragPayload(SearchSuggestionItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Uri))
            return null;

        return item.Type switch
        {
            SearchSuggestionType.Track when IsSpotifyTrack(item.Uri)
                => new TrackDragPayload(new[] { item.Uri }),
            SearchSuggestionType.Episode when IsSpotifyEpisode(item.Uri)
                => new TrackDragPayload(new[] { item.Uri }),
            SearchSuggestionType.Album when item.Uri.StartsWith("spotify:album:", StringComparison.Ordinal)
                => new AlbumDragPayload(item.Uri, item.Title, item.ImageUrl),
            SearchSuggestionType.Artist when item.Uri.StartsWith("spotify:artist:", StringComparison.Ordinal)
                => new ArtistDragPayload(item.Uri, item.Title),
            SearchSuggestionType.Playlist when item.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)
                => new PlaylistDragPayload(item.Uri, item.Title),
            SearchSuggestionType.Podcast when item.Uri.StartsWith("spotify:show:", StringComparison.Ordinal)
                => new ShowDragPayload(item.Uri, item.Title, item.ImageUrl),
            SearchSuggestionType.LinkAction when string.Equals(item.Uri, LikedSongsDragPayload.LikedSongsUri, StringComparison.Ordinal)
                => new LikedSongsDragPayload(),
            _ => null
        };
    }

    public static IReadOnlyList<ContextMenuItemModel> BuildContextMenu(SearchSuggestionItem item)
    {
        if (item.Type == SearchSuggestionType.Track && IsSpotifyTrack(item.Uri))
            return TrackContextMenuBuilder.Build(new SearchSuggestionTrackItem(item));

        if (ShouldUseCardMenu(item))
        {
            return CardContextMenuBuilder.BuildForUri(
                item.Uri,
                item.Title,
                item.ImageUrl,
                openInNewTab => OpenSuggestion(item, openInNewTab));
        }

        if (CanOpenSuggestion(item))
            return BuildOpenMenu(item);

        return Array.Empty<ContextMenuItemModel>();
    }

    private static bool ShouldUseCardMenu(SearchSuggestionItem item)
        => item.Type switch
        {
            SearchSuggestionType.Album when item.Uri.StartsWith("spotify:album:", StringComparison.Ordinal) => true,
            SearchSuggestionType.Artist when item.Uri.StartsWith("spotify:artist:", StringComparison.Ordinal) => true,
            SearchSuggestionType.Playlist when item.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal) => true,
            SearchSuggestionType.Podcast when item.Uri.StartsWith("spotify:show:", StringComparison.Ordinal) => true,
            SearchSuggestionType.Episode when IsSpotifyEpisode(item.Uri) => true,
            SearchSuggestionType.LinkAction when string.Equals(item.Uri, LikedSongsDragPayload.LikedSongsUri, StringComparison.Ordinal) => true,
            _ => false
        };

    private static bool CanOpenSuggestion(SearchSuggestionItem item)
        => item.Type is SearchSuggestionType.User
            or SearchSuggestionType.Genre
            or SearchSuggestionType.LinkAction;

    private static IReadOnlyList<ContextMenuItemModel> BuildOpenMenu(SearchSuggestionItem item)
    {
        var items = new List<ContextMenuItemModel>
        {
            new()
            {
                Text = AppLocalization.GetString("CardMenu_Open"),
                Glyph = FluentGlyphs.Open,
                IsPrimary = true,
                Invoke = () => OpenSuggestion(item, openInNewTab: false)
            },
            new()
            {
                Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
                Glyph = FluentGlyphs.OpenInNewTab,
                IsPrimary = true,
                Invoke = () => OpenSuggestion(item, openInNewTab: true)
            }
        };

        return items;
    }

    private static void OpenSuggestion(SearchSuggestionItem item, bool openInNewTab)
    {
        switch (item.Type)
        {
            case SearchSuggestionType.Album:
                NavigationHelpers.OpenAlbum(ToParameter(item), item.Title, openInNewTab);
                break;
            case SearchSuggestionType.Artist:
                NavigationHelpers.OpenArtist(ToParameter(item), item.Title, openInNewTab);
                break;
            case SearchSuggestionType.Playlist:
                NavigationHelpers.OpenPlaylist(ToParameter(item), item.Title, openInNewTab);
                break;
            case SearchSuggestionType.Podcast:
                NavigationHelpers.OpenShowPage(ToParameter(item), openInNewTab);
                break;
            case SearchSuggestionType.Episode:
                NavigationHelpers.OpenEpisodePage(item.Uri, item.Title, item.ImageUrl, openInNewTab: openInNewTab);
                break;
            case SearchSuggestionType.User:
                NavigationHelpers.OpenProfile(ToParameter(item), item.Title, openInNewTab);
                break;
            case SearchSuggestionType.Genre:
                NavigationHelpers.OpenBrowsePage(ToParameter(item), openInNewTab);
                break;
            case SearchSuggestionType.LinkAction:
                if (string.Equals(item.Uri, "spotify:collection:your-episodes", StringComparison.Ordinal))
                    NavigationHelpers.OpenYourEpisodes(openInNewTab);
                else if (string.Equals(item.Uri, LikedSongsDragPayload.LikedSongsUri, StringComparison.Ordinal))
                    NavigationHelpers.OpenLikedSongs(openInNewTab);
                break;
        }
    }

    private static ContentNavigationParameter ToParameter(SearchSuggestionItem item)
        => new()
        {
            Uri = item.Uri,
            Title = item.Title,
            Subtitle = item.Subtitle,
            ImageUrl = item.ImageUrl
        };

    private static bool IsSpotifyTrack(string? uri)
        => uri?.StartsWith("spotify:track:", StringComparison.Ordinal) == true;

    private static bool IsSpotifyEpisode(string? uri)
        => uri?.StartsWith("spotify:episode:", StringComparison.Ordinal) == true;

    private sealed class SearchSuggestionTrackItem : ITrackItem
    {
        private readonly SearchSuggestionItem _item;

        public SearchSuggestionTrackItem(SearchSuggestionItem item)
        {
            _item = item;
        }

        public string Id => BareId(Uri);
        public string Uri => _item.Uri;
        public string Title => _item.Title;
        public string ArtistName => ExtractArtistName(_item.Subtitle);
        public string ArtistId => string.Empty;
        public string AlbumName => string.Empty;
        public string AlbumId => string.Empty;
        public string? ImageUrl => _item.ImageUrl;
        public TimeSpan Duration => TimeSpan.Zero;
        public bool IsExplicit => false;
        public string DurationFormatted => string.Empty;
        public int OriginalIndex => 0;
        public bool IsLoaded => true;

        public bool IsLiked
        {
            get => Ioc.Default.GetService<ITrackLikeService>()?.IsSaved(SavedItemType.Track, Uri) == true;
            set { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string BareId(string uri)
        {
            var index = uri.LastIndexOf(':');
            return index >= 0 && index < uri.Length - 1 ? uri[(index + 1)..] : uri;
        }

        private static string ExtractArtistName(string? subtitle)
        {
            if (string.IsNullOrWhiteSpace(subtitle))
                return string.Empty;

            var text = subtitle.Trim();
            const string songPrefix = "Song";
            if (text.StartsWith(songPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var separatorIndex = text.IndexOf('·');
                if (separatorIndex >= 0 && separatorIndex < text.Length - 1)
                    return text[(separatorIndex + 1)..].Trim();
            }

            return text;
        }
    }
}
