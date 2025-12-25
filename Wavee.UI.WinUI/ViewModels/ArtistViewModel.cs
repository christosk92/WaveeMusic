using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ArtistViewModel : ObservableObject, ITabBarItemContent
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _artistId;

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private string? _artistImageUrl;

    [ObservableProperty]
    private string? _headerImageUrl;

    [ObservableProperty]
    private int _monthlyListeners;

    [ObservableProperty]
    private bool _isFollowing;

    [ObservableProperty]
    private ObservableCollection<ArtistTopTrack> _topTracks = [];

    [ObservableProperty]
    private ObservableCollection<ArtistAlbum> _albums = [];

    [ObservableProperty]
    private ObservableCollection<RelatedArtist> _relatedArtists = [];

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistViewModel()
    {
    }

    public void Initialize(string artistId)
    {
        ArtistId = artistId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Artist, artistId)
        {
            Title = "Artist"
        };
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading || string.IsNullOrEmpty(ArtistId)) return;

        try
        {
            IsLoading = true;
            // TODO: Load artist data from Wavee core
            await Task.Delay(100); // Placeholder
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        IsFollowing = !IsFollowing;
        // TODO: Update follow state via Wavee core
    }

    [RelayCommand]
    private void PlayTopTracks()
    {
        // TODO: Play artist's top tracks via Wavee core
    }

    [RelayCommand]
    private void OpenAlbum(string albumId)
    {
        Helpers.Navigation.NavigationHelpers.OpenAlbum(albumId, "Album");
    }

    [RelayCommand]
    private void OpenRelatedArtist(string artistId)
    {
        var artist = RelatedArtists.FirstOrDefault(a => a.Id == artistId);
        Helpers.Navigation.NavigationHelpers.OpenArtist(artistId, artist?.Name ?? "Artist");
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(ArtistName))
        {
            TabItemParameter.Title = ArtistName;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    partial void OnArtistNameChanged(string? value)
    {
        UpdateTabTitle();
    }
}

public sealed class ArtistTopTrack
{
    public string? Id { get; set; }
    public int Index { get; set; }
    public string? Title { get; set; }
    public string? AlbumName { get; set; }
    public string? AlbumImageUrl { get; set; }
    public TimeSpan Duration { get; set; }
    public long PlayCount { get; set; }
}

public sealed class ArtistAlbum
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? ImageUrl { get; set; }
    public int Year { get; set; }
    public string? AlbumType { get; set; }
}

public sealed class RelatedArtist
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ImageUrl { get; set; }
}
