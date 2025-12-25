using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, ITabBarItemContent
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _greeting = "Good morning";

    [ObservableProperty]
    private ObservableCollection<HomeSection> _sections = [];

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomeViewModel()
    {
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Home, null)
        {
            Title = "Home"
        };

        UpdateGreeting();
    }

    private void UpdateGreeting()
    {
        var hour = DateTime.Now.Hour;
        Greeting = hour switch
        {
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading) return;

        try
        {
            IsLoading = true;
            // TODO: Load home content from Wavee core
            await Task.Delay(100); // Placeholder
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenArtist(string artistId)
    {
        Helpers.Navigation.NavigationHelpers.OpenArtist(artistId, "Artist");
    }

    [RelayCommand]
    private void OpenAlbum(string albumId)
    {
        Helpers.Navigation.NavigationHelpers.OpenAlbum(albumId, "Album");
    }

    [RelayCommand]
    private void OpenPlaylist(string playlistId)
    {
        Helpers.Navigation.NavigationHelpers.OpenPlaylist(playlistId, "Playlist");
    }
}

public sealed class HomeSection
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public ObservableCollection<HomeSectionItem> Items { get; set; } = [];
}

public sealed class HomeSectionItem
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? ImageUrl { get; set; }
    public string? Type { get; set; }
}
