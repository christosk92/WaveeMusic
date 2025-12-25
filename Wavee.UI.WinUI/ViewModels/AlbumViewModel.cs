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

public sealed partial class AlbumViewModel : ObservableObject, ITabBarItemContent
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _albumId;

    [ObservableProperty]
    private string? _albumName;

    [ObservableProperty]
    private string? _albumImageUrl;

    [ObservableProperty]
    private string? _artistId;

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private int _year;

    [ObservableProperty]
    private string? _albumType;

    [ObservableProperty]
    private int _trackCount;

    [ObservableProperty]
    private TimeSpan _totalDuration;

    [ObservableProperty]
    private bool _isSaved;

    [ObservableProperty]
    private ObservableCollection<AlbumTrack> _tracks = [];

    [ObservableProperty]
    private ObservableCollection<ArtistAlbum> _moreByArtist = [];

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public AlbumViewModel()
    {
    }

    public void Initialize(string albumId)
    {
        AlbumId = albumId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Album, albumId)
        {
            Title = "Album"
        };
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading || string.IsNullOrEmpty(AlbumId)) return;

        try
        {
            IsLoading = true;
            // TODO: Load album data from Wavee core
            await Task.Delay(100); // Placeholder
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleSave()
    {
        IsSaved = !IsSaved;
        // TODO: Update saved state via Wavee core
    }

    [RelayCommand]
    private void PlayAlbum()
    {
        // TODO: Play album via Wavee core
    }

    [RelayCommand]
    private void ShuffleAlbum()
    {
        // TODO: Shuffle play album via Wavee core
    }

    [RelayCommand]
    private void PlayTrack(AlbumTrack? track)
    {
        if (track is null) return;
        // TODO: Play specific track via Wavee core
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (!string.IsNullOrEmpty(ArtistId))
        {
            Helpers.Navigation.NavigationHelpers.OpenArtist(ArtistId, ArtistName ?? "Artist");
        }
    }

    [RelayCommand]
    private void OpenRelatedAlbum(string albumId)
    {
        var album = MoreByArtist.FirstOrDefault(a => a.Id == albumId);
        Helpers.Navigation.NavigationHelpers.OpenAlbum(albumId, album?.Title ?? "Album");
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(AlbumName))
        {
            TabItemParameter.Title = AlbumName;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    partial void OnAlbumNameChanged(string? value)
    {
        UpdateTabTitle();
    }
}

public sealed class AlbumTrack
{
    public string? Id { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    public string? ArtistId { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsExplicit { get; set; }
    public bool IsPlayable { get; set; } = true;
}
