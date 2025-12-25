using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class StartPageViewModel : ObservableObject
{
    public ObservableCollection<QuickAccessItem> QuickAccessItems { get; } = [];
    public ObservableCollection<RecentItem> RecentItems { get; } = [];

    public StartPageViewModel()
    {
        LoadQuickAccess();
        LoadRecentItems();
    }

    private void LoadQuickAccess()
    {
        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Home",
            Glyph = "\uE80F",
            Action = () => NavigationHelpers.OpenHome()
        });

        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Search",
            Glyph = "\uE721",
            Action = () => NavigationHelpers.OpenSearch()
        });

        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Library",
            Glyph = "\uE8F1",
            Action = () => NavigationHelpers.OpenLibrary()
        });

        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Liked Songs",
            Glyph = "\uEB52",
            Action = () => NavigationHelpers.OpenLikedSongs()
        });
    }

    private void LoadRecentItems()
    {
        // Mock data for now
        RecentItems.Add(new RecentItem
        {
            Title = "Chill Vibes",
            Subtitle = "Playlist",
            ImageUrl = null,
            ItemType = "playlist",
            ItemId = "mock-playlist-1"
        });

        RecentItems.Add(new RecentItem
        {
            Title = "The Dark Side of the Moon",
            Subtitle = "Pink Floyd",
            ImageUrl = null,
            ItemType = "album",
            ItemId = "mock-album-1"
        });

        RecentItems.Add(new RecentItem
        {
            Title = "Bohemian Rhapsody",
            Subtitle = "Queen",
            ImageUrl = null,
            ItemType = "track",
            ItemId = "mock-track-1"
        });

        RecentItems.Add(new RecentItem
        {
            Title = "Daft Punk",
            Subtitle = "Artist",
            ImageUrl = null,
            ItemType = "artist",
            ItemId = "mock-artist-1"
        });

        RecentItems.Add(new RecentItem
        {
            Title = "Today's Top Hits",
            Subtitle = "Playlist",
            ImageUrl = null,
            ItemType = "playlist",
            ItemId = "mock-playlist-2"
        });
    }

    [RelayCommand]
    private void Search(string query)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            NavigationHelpers.OpenSearch(query);
        }
    }
}

public class QuickAccessItem
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required Action Action { get; init; }
}

public class RecentItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public required string ItemType { get; init; }
    public required string ItemId { get; init; }
}
