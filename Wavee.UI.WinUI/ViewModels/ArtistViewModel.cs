using System;
using System.Collections.Generic;
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
    private readonly ILibraryDataService _libraryDataService;

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

    // Pagination properties
    private const int RowsPerPage = 5;

    [ObservableProperty]
    private int _columnCount = 2; // Updated by view based on window width

    private int TracksPerPage => RowsPerPage * ColumnCount; // Dynamic: 10 or 5

    [ObservableProperty]
    private int _currentPage;

    public int TotalPages => TopTracks.Count == 0 ? 0 : (int)Math.Ceiling((double)TopTracks.Count / TracksPerPage);

    // Split tracks into two columns for current page
    public IEnumerable<ArtistTopTrack> Column1Tracks =>
        TopTracks.Skip(CurrentPage * TracksPerPage).Take(RowsPerPage);

    public IEnumerable<ArtistTopTrack> Column2Tracks =>
        TopTracks.Skip(CurrentPage * TracksPerPage + RowsPerPage).Take(RowsPerPage);

    partial void OnColumnCountChanged(int value)
    {
        // Reset to page 0 and recalculate when layout changes
        CurrentPage = 0;
        NotifyPaginationChanged();
    }

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistViewModel(ILibraryDataService libraryDataService)
    {
        _libraryDataService = libraryDataService;
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

            // Get artist info from library
            var artists = await _libraryDataService.GetArtistsAsync();
            var artist = artists.FirstOrDefault(a => a.Id == ArtistId);
            if (artist != null)
            {
                ArtistName = artist.Name;
                ArtistImageUrl = artist.ImageUrl;
                MonthlyListeners = artist.FollowerCount;
            }

            // Get top tracks
            var topTracks = await _libraryDataService.GetArtistTopTracksAsync(ArtistId);
            TopTracks.Clear();
            var trackIndex = 1;
            foreach (var track in topTracks)
            {
                TopTracks.Add(new ArtistTopTrack
                {
                    Id = track.Id,
                    Index = trackIndex++,
                    Title = track.Title,
                    AlbumName = track.AlbumName,
                    AlbumImageUrl = track.AlbumImageUrl,
                    Duration = track.Duration,
                    PlayCount = track.PlayCount
                });
            }

            // Get albums/discography
            var albums = await _libraryDataService.GetArtistAlbumsAsync(ArtistId);
            Albums.Clear();
            foreach (var album in albums)
            {
                Albums.Add(new ArtistAlbum
                {
                    Id = album.Id,
                    Title = album.Name,
                    ImageUrl = album.ImageUrl,
                    Year = album.Year,
                    AlbumType = album.AlbumType
                });
            }

            // Initialize pagination
            CurrentPage = 0;
            NotifyPaginationChanged();
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

    private void NotifyPaginationChanged()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(Column1Tracks));
        OnPropertyChanged(nameof(Column2Tracks));
    }

    partial void OnCurrentPageChanged(int value)
    {
        NotifyPaginationChanged();
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
