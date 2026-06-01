using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Controls.Library;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public enum ArtistsLibraryStage
{
    Artists,
    Details,
    Tracks
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ArtistsLibraryViewModel : DualSourceLibraryViewModelBase<LibraryArtistDto, LikedArtistDto>, ITrackListViewModel, IDisposable
{
    protected override string SavedPreferencesKey => "artists.saved";
    protected override string LikedPreferencesKey => "artists.liked";

    private readonly ILibraryDataService _libraryDataService;
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly IPlaybackService _playbackService;
    private readonly ILikedSongsGroupingCache _grouping;
    private bool _disposed;
    private IReadOnlyDictionary<string, DateTimeOffset> _artistRecents =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial bool IsLoadingDetails { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<LibraryArtistDto> Artists { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<LibraryArtistDto> FilteredArtists { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSavedArtistPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowSavedArtistDetails))]
    public partial LibraryArtistDto? SelectedArtist { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<LikedArtistDto> LikedArtists { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<LikedArtistDto> FilteredLikedArtists { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLikedArtistPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowLikedArtistDetails))]
    public partial LikedArtistDto? SelectedLikedArtist { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<LikedSongDto> SelectedLikedArtistTracks { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<ArtistAlbumGroupViewModel> AlbumGroups { get; set; } = [];

    // Wrapper properties for selected artist (avoids null reference in x:Bind)
    [ObservableProperty]
    public partial string SelectedArtistName { get; set; } = "";

    [ObservableProperty]
    public partial string? SelectedArtistImageUrl { get; set; }

    [ObservableProperty]
    public partial string SelectedArtistAddedAt { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedArtistAlbumCount { get; set; }

    // Discography filter
    [ObservableProperty]
    public partial bool ShowSavedOnly { get; set; }

    private List<LibraryArtistAlbumDto> _allAlbums = [];

    // Action-row counts. Plain getters over _allAlbums; recounted by raising
    // PropertyChanged after _allAlbums is reassigned in
    // LoadSelectedArtistDetailsAsync. Used by the Saved-only toggle's
    // "Saved only (N)" label so the filter's effect is obvious.
    //
    // SavedAlbumCount mirrors the toggle's actual predicate
    // (IsInLibrary = directly-saved OR contains-liked-songs) so the label
    // matches what the user sees after enabling the filter.
    public int SavedAlbumCount => _allAlbums.Count(a => a.IsInLibrary);
    public int TotalAlbumCount => _allAlbums.Count;
    public string SavedOnlyButtonLabel => $"Saved only ({SavedAlbumCount})";

    // Tracks panel (third column) properties
    [ObservableProperty]
    public partial ArtistAlbumItemViewModel? SelectedAlbumForTracks { get; set; }

    [ObservableProperty]
    public partial bool IsTracksPanelVisible { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<AlbumTrackDto> SelectedAlbumTracks { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoadingSelectedAlbumTracks { get; set; }

    // Wrapper properties for selected album
    [ObservableProperty]
    public partial string SelectedAlbumName { get; set; } = "";

    [ObservableProperty]
    public partial string? SelectedAlbumImageUrl { get; set; }

    [ObservableProperty]
    public partial int SelectedAlbumYear { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWideLayout))]
    [NotifyPropertyChangedFor(nameof(IsNarrowLayout))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowAlbumTracksStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    [NotifyPropertyChangedFor(nameof(IsSavedWide))]
    [NotifyPropertyChangedFor(nameof(IsLikedWide))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowAlbumTracksStage))]
    public partial bool UseNarrowLayout { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowAlbumTracksStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowAlbumTracksStage))]
    public partial ArtistsLibraryStage NarrowStage { get; set; } = ArtistsLibraryStage.Artists;

    public ObservableCollection<string> BreadcrumbItems { get; } = [];

    /// <summary>Declarative action rows for the shared <c>LibraryDetailPanel</c>.</summary>
    public ObservableCollection<LibraryDetailAction> SavedArtistDetailActions { get; } = [];
    public ObservableCollection<LibraryDetailAction> LikedArtistDetailActions { get; } = [];
    private LibraryDetailAction? _savedOnlyAction;

    public bool IsWideLayout => !UseNarrowLayout;
    public bool IsNarrowLayout => UseNarrowLayout;
    public bool ShowNarrowArtistsStage => UseNarrowLayout && NarrowStage == ArtistsLibraryStage.Artists;
    public bool ShowNarrowArtistDetailsStage => UseNarrowLayout && NarrowStage == ArtistsLibraryStage.Details;
    public bool ShowNarrowAlbumTracksStage => UseNarrowLayout && NarrowStage == ArtistsLibraryStage.Tracks;
    public bool ShowBreadcrumbBar => UseNarrowLayout;
    public bool IsSavedWide => IsSavedSource && IsWideLayout;
    public bool IsLikedWide => IsLikedSource && IsWideLayout;
    public bool ShowSavedArtistPlaceholder => IsSavedSource && SelectedArtist == null;
    public bool ShowSavedArtistDetails => IsSavedSource && SelectedArtist != null;
    public bool ShowLikedArtistPlaceholder => IsLikedSource && SelectedLikedArtist == null;
    public bool ShowLikedArtistDetails => IsLikedSource && SelectedLikedArtist != null;
    public bool ShowSavedNarrowArtistsStage => IsSavedSource && ShowNarrowArtistsStage;
    public bool ShowLikedNarrowArtistsStage => IsLikedSource && ShowNarrowArtistsStage;
    public bool ShowSavedNarrowArtistDetailsStage => IsSavedSource && ShowNarrowArtistDetailsStage;
    public bool ShowLikedNarrowArtistDetailsStage => IsLikedSource && ShowNarrowArtistDetailsStage;
    public bool ShowSavedNarrowAlbumTracksStage => IsSavedSource && ShowNarrowAlbumTracksStage;

    public ILibraryDataService LibraryDataService => _libraryDataService;

    public ArtistsLibraryViewModel(
        ILibraryDataService libraryDataService,
        IArtistService artistService,
        IAlbumService albumService,
        IPlaybackService playbackService,
        ILikedSongsGroupingCache grouping,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        LibraryRecentsService? libraryRecents = null)
        : base(settingsService, likeService, libraryRecents, DispatcherQueue.GetForCurrentThread())
    {
        _libraryDataService = libraryDataService;
        _artistService = artistService;
        _albumService = albumService;
        _playbackService = playbackService;
        _grouping = grouping;

        LoadPreferences();
        BuildArtistDetailActions();

        AttachLongLivedServices();
        if (LibraryRecents != null)
            _ = PrefetchRecentsAsync();
    }

    private void BuildArtistDetailActions()
    {
        SavedArtistDetailActions.Add(new LibraryDetailAction { Label = "Play", Glyph = FluentGlyphs.Play, IsAccent = true, Command = PlayArtistCommand });
        SavedArtistDetailActions.Add(new LibraryDetailAction { Label = "Shuffle", Glyph = FluentGlyphs.Shuffle, Command = ShuffleArtistCommand });
        SavedArtistDetailActions.Add(new LibraryDetailAction { Label = "View artist", Glyph = FluentGlyphs.ShowFilled, Command = OpenArtistDetailsCommand });
        SavedArtistDetailActions.Add(new LibraryDetailAction { Label = "Following", Glyph = FluentGlyphs.CheckMark, IsToggle = true, IsChecked = true, Command = ToggleFollowSelectedArtistCommand });
        _savedOnlyAction = new LibraryDetailAction { Label = "Saved only", Glyph = FluentGlyphs.Pin, IsToggle = true, IsChecked = ShowSavedOnly, Command = ToggleSavedOnlyCommand };
        SavedArtistDetailActions.Add(_savedOnlyAction);

        LikedArtistDetailActions.Add(new LibraryDetailAction { Label = "Play liked", Glyph = FluentGlyphs.Play, IsAccent = true, Command = PlayLikedArtistTracksCommand });
        LikedArtistDetailActions.Add(new LibraryDetailAction { Label = "Shuffle", Glyph = FluentGlyphs.Shuffle, Command = ShuffleLikedArtistTracksCommand });
        LikedArtistDetailActions.Add(new LibraryDetailAction { Label = "Open artist", Glyph = FluentGlyphs.Open, Command = OpenArtistDetailsCommand });
    }

    [RelayCommand]
    private void ToggleFollowSelectedArtist()
    {
        if (SelectedArtist is { } artist && LikeService is not null)
            LikeService.ToggleSave(SavedItemType.Artist, artist.Id, currentlySaved: true);
    }

    [RelayCommand]
    private void ToggleSavedOnly()
    {
        if (_savedOnlyAction is not null)
            ShowSavedOnly = _savedOnlyAction.IsChecked;
    }

    private async Task PrefetchRecentsAsync()
    {
        if (LibraryRecents == null) return;
        try
        {
            var map = await LibraryRecents.GetArtistRecentsAsync().ConfigureAwait(false);
            _artistRecents = map;
            DispatcherQueue.TryEnqueue(ApplyFilter);
        }
        catch
        {
            // Ignore — sort falls back to AddedAt.
        }
    }

    private void OnLibraryRecentsChanged()
    {
        if (_disposed || LibraryRecents == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var map = await LibraryRecents.GetArtistRecentsAsync().ConfigureAwait(false);
                _artistRecents = map;
                DispatcherQueue.TryEnqueue(ApplyFilter);
            }
            catch { /* ignore */ }
        });
    }

    // Creator + ReleaseDate don't apply to artists; the panel hides them, but guard
    // against a stale settings value (e.g. migrated from an album tab preference).
    protected override bool IsAllowedSortKey(LibrarySortBy key) =>
        key is LibrarySortBy.Recents or LibrarySortBy.RecentlyAdded or LibrarySortBy.Alphabetical;

    protected override LibrarySource ReadPersistedSource(AppSettings settings) =>
        string.Equals(settings.ArtistsLibrarySource, nameof(LibrarySource.FromLikedSongs), StringComparison.OrdinalIgnoreCase)
            ? LibrarySource.FromLikedSongs
            : LibrarySource.Saved;

    protected override void WritePersistedSource(AppSettings settings, LibrarySource source)
    {
        settings.ArtistsLibrarySource = source.ToString();
    }

    protected override void ApplyFilterCore()
    {
        ApplyFilter();
    }

    protected override void OnSaveStateChangedFromBase()
    {
        OnSaveStateChanged();
    }

    protected override void OnRecentsChangedFromBase()
    {
        OnLibraryRecentsChanged();
    }

    protected override void OnSourceModeChangedCore(LibrarySource oldValue, LibrarySource newValue)
    {
        OnPropertyChanged(nameof(IsSavedWide));
        OnPropertyChanged(nameof(IsLikedWide));
        OnPropertyChanged(nameof(ShowSavedArtistPlaceholder));
        OnPropertyChanged(nameof(ShowSavedArtistDetails));
        OnPropertyChanged(nameof(ShowLikedArtistPlaceholder));
        OnPropertyChanged(nameof(ShowLikedArtistDetails));
        OnPropertyChanged(nameof(ShowSavedNarrowArtistsStage));
        OnPropertyChanged(nameof(ShowLikedNarrowArtistsStage));
        OnPropertyChanged(nameof(ShowSavedNarrowArtistDetailsStage));
        OnPropertyChanged(nameof(ShowLikedNarrowArtistDetailsStage));
        OnPropertyChanged(nameof(ShowSavedNarrowAlbumTracksStage));

        SelectedAlbumForTracks = null;
        if (newValue == LibrarySource.Saved)
        {
            SelectedLikedArtist = null;
            if (SelectedArtist == null && FilteredArtists.Count > 0)
                SelectedArtist = FilteredArtists[0];
        }
        else
        {
            SelectedArtist = null;
            if (!LikedSideLoaded)
                _ = LoadLikedArtistsAsync(preserveSelection: false);
            else
            {
                ApplyFilter();
                if (SelectedLikedArtist == null && FilteredLikedArtists.Count > 0)
                    SelectedLikedArtist = FilteredLikedArtists[0];
            }
        }

        UpdateBreadcrumbs();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Skip if already loaded (for page cache restoration)
        if (IsLoading || Artists.Count > 0) return;

        await LoadDataAsync(preserveSelection: false);
        if (SourceMode == LibrarySource.FromLikedSongs && !LikedSideLoaded)
            await LoadLikedArtistsAsync(preserveSelection: false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        await LoadDataAsync(preserveSelection: true);
        if (LikedSideLoaded)
            await LoadLikedArtistsAsync(preserveSelection: true);
    }

    private async Task LoadDataAsync(bool preserveSelection)
    {
        var previousSelectedId = preserveSelection ? SelectedArtist?.Id : null;

        try
        {
            IsLoading = true;

            // Load artists
            var artists = await _libraryDataService.GetArtistsAsync();

            Artists.Clear();
            foreach (var artist in artists)
            {
                Artists.Add(artist);
            }

            ApplyFilter();

            // Restore previous selection or select first
            if (previousSelectedId != null)
            {
                SelectedArtist = FilteredArtists.FirstOrDefault(a => a.Id == previousSelectedId);
            }

            if (SourceMode == LibrarySource.Saved && SelectedArtist == null && FilteredArtists.Count > 0)
            {
                SelectedArtist = FilteredArtists[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadLikedArtistsAsync(bool preserveSelection)
    {
        var previousSelectedId = preserveSelection ? SelectedLikedArtist?.Id : null;

        try
        {
            IsLoading = true;
            // Shared cache: one liked-songs fetch + one grouping reused across the
            // Albums and Artists tabs; rebuilt only on save-state change.
            var grouped = await _grouping.GetArtistsAsync();

            LikedArtists.ApplyKeyedDiff(grouped, a => a.Id, keyComparer: StringComparer.OrdinalIgnoreCase);

            LikedSideLoaded = true;
            ApplyFilter();

            if (SourceMode == LibrarySource.FromLikedSongs)
            {
                if (previousSelectedId != null)
                    SelectedLikedArtist = FilteredLikedArtists.FirstOrDefault(a =>
                        string.Equals(a.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase));

                if (SelectedLikedArtist == null && FilteredLikedArtists.Count > 0)
                    SelectedLikedArtist = FilteredLikedArtists[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayArtistAsync()
    {
        if (SelectedArtist == null) return;
        await _playbackService.PlayContextAsync(
            SelectedArtist.Id,
            new PlayContextOptions { PlayOriginFeature = "artist_library" });
    }

    [RelayCommand]
    private async Task ShuffleArtistAsync()
    {
        if (SelectedArtist == null) return;
        await _playbackService.PlayContextAsync(
            SelectedArtist.Id,
            new PlayContextOptions { Shuffle = true, PlayOriginFeature = "artist_library" });
    }

    [RelayCommand]
    private async Task PlayLikedArtistTracksAsync()
    {
        if (SelectedLikedArtist is not { } liked) return;
        var trackUris = liked.LikedSongs.Select(s => s.Uri).ToList();
        if (trackUris.Count == 0) return;

        var contextInfo = new PlaybackContextInfo
        {
            ContextUri = liked.CanOpenArtist ? liked.Id : "spotify:collection",
            Type = PlaybackContextType.Artist,
            Name = liked.Name,
            ImageUrl = liked.ImageUrl,
        };
        await _playbackService.PlayTracksAsync(trackUris, 0, contextInfo);
    }

    [RelayCommand]
    private async Task ShuffleLikedArtistTracksAsync()
    {
        if (SelectedLikedArtist is not { } liked) return;
        var trackUris = liked.LikedSongs.Select(s => s.Uri).ToList();
        if (trackUris.Count == 0) return;

        var rng = Random.Shared;
        for (var i = trackUris.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (trackUris[i], trackUris[j]) = (trackUris[j], trackUris[i]);
        }

        var contextInfo = new PlaybackContextInfo
        {
            ContextUri = liked.CanOpenArtist ? liked.Id : "spotify:collection",
            Type = PlaybackContextType.Artist,
            Name = liked.Name,
            ImageUrl = liked.ImageUrl,
        };
        await _playbackService.PlayTracksAsync(trackUris, 0, contextInfo);
    }

    [RelayCommand]
    private void OpenArtistDetails()
    {
        if (SelectedLikedArtist is { } liked)
        {
            if (!liked.CanOpenArtist) return;

            Helpers.Navigation.NavigationHelpers.OpenArtist(
                new Data.Parameters.ContentNavigationParameter
                {
                    Uri = liked.Id,
                    Title = liked.Name,
                    ImageUrl = liked.ImageUrl,
                },
                liked.Name);
            return;
        }

        if (SelectedArtist == null) return;
        // Pass the lean library data via ContentNavigationParameter so ArtistPage
        // can PrefillFrom(...) and render hero name + avatar in the first frame
        // without waiting on ArtistStore.
        Helpers.Navigation.NavigationHelpers.OpenArtist(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = SelectedArtist.Id,
                Title = SelectedArtist.Name,
                ImageUrl = SelectedArtist.ImageUrl,
            },
            SelectedArtist.Name);
    }

    [RelayCommand]
    private void SelectAlbumForTracks(ArtistAlbumItemViewModel? album)
    {
        SelectedAlbumForTracks = album;
        if (UseNarrowLayout && album != null)
        {
            SetNarrowStage(ArtistsLibraryStage.Tracks);
        }
    }

    [RelayCommand]
    private void CloseTracksPanel()
    {
        SelectedAlbumForTracks = null;
        if (UseNarrowLayout)
        {
            SetNarrowStage(SelectedArtist != null
                ? ArtistsLibraryStage.Details
                : ArtistsLibraryStage.Artists);
        }
    }

    [RelayCommand]
    private void OpenSelectedAlbum()
    {
        if (SelectedAlbumForTracks == null) return;
        // Pass the lean album data + the parent artist's name as Subtitle so
        // AlbumPage's hero shows cover + title + artist before the AlbumStore
        // Pathfinder fetch lands.
        var album = SelectedAlbumForTracks.Album;
        Helpers.Navigation.NavigationHelpers.OpenAlbum(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = album.Id,
                Title = album.Name,
                Subtitle = SelectedArtist?.Name,
                ImageUrl = album.ImageUrl,
            },
            album.Name);
    }

    partial void OnSelectedAlbumForTracksChanged(ArtistAlbumItemViewModel? value)
    {
        IsTracksPanelVisible = value != null;
        if (value != null)
        {
            SelectedAlbumName = value.Album.Name;
            SelectedAlbumImageUrl = value.Album.ImageUrl;
            SelectedAlbumYear = value.Album.Year;
            _ = LoadSelectedAlbumTracksAsync(value.Album.Id);
        }
        else
        {
            SelectedAlbumTracks.Clear();
        }

        if (UseNarrowLayout)
        {
            NarrowStage = value != null
                ? ArtistsLibraryStage.Tracks
                : SelectedArtist != null
                    ? ArtistsLibraryStage.Details
                    : ArtistsLibraryStage.Artists;
        }

        UpdateBreadcrumbs();
    }

    private async Task LoadSelectedAlbumTracksAsync(string albumId)
    {
        try
        {
            IsLoadingSelectedAlbumTracks = true;
            SelectedAlbumTracks.Clear();

            var tracks = await _albumService.GetTracksAsync(albumId);
            foreach (var track in tracks)
            {
                SelectedAlbumTracks.Add(track);
            }
        }
        finally
        {
            IsLoadingSelectedAlbumTracks = false;
        }
    }

    partial void OnSelectedArtistChanged(LibraryArtistDto? value)
    {
        if (value != null && SelectedLikedArtist != null)
            SelectedLikedArtist = null;

        // Close tracks panel when artist changes
        SelectedAlbumForTracks = null;

        // Update wrapper properties
        SelectedArtistName = value?.Name ?? "";
        SelectedArtistImageUrl = value?.ImageUrl;
        SelectedArtistAddedAt = value?.AddedAtFormatted ?? "";
        SelectedArtistAlbumCount = value?.AlbumCount ?? 0;

        if (UseNarrowLayout && value == null)
        {
            NarrowStage = ArtistsLibraryStage.Artists;
        }

        UpdateBreadcrumbs();

        if (value != null)
            _ = LoadSelectedArtistDetailsAsync();
    }

    partial void OnSelectedLikedArtistChanged(LikedArtistDto? value)
    {
        if (value != null && SelectedArtist != null)
            SelectedArtist = null;

        SelectedAlbumForTracks = null;
        AlbumGroups.Clear();
        _allAlbums.Clear();

        SelectedArtistName = value?.Name ?? "";
        SelectedArtistImageUrl = value?.ImageUrl;
        SelectedArtistAddedAt = value != null
            ? BuildSelectedLikedArtistMetadata(value)
            : "";
        SelectedArtistAlbumCount = 0;

        SelectedLikedArtistTracks.Clear();
        if (value != null)
        {
            foreach (var track in value.LikedSongs)
                SelectedLikedArtistTracks.Add(track);
        }

        if (UseNarrowLayout && value == null && SelectedArtist == null)
            NarrowStage = ArtistsLibraryStage.Artists;

        UpdateBreadcrumbs();
    }

    private string BuildSelectedLikedArtistMetadata(LikedArtistDto artist)
    {
        var parts = new List<string> { artist.LikedSongCountLabel };
        if (artist.MostRecentLikedAt > DateTimeOffset.MinValue)
            parts.Add($"Last liked {artist.MostRecentLikedAt.LocalDateTime:MMM d, yyyy}");
        if (_artistRecents.TryGetValue(artist.Id, out var lastPlayed))
            parts.Add(FormatRecentsSubtitle(lastPlayed));
        return string.Join(" \u2022 ", parts);
    }

    public void SetNarrowLayout(bool isNarrow, bool preserveContext)
    {
        if (UseNarrowLayout == isNarrow)
        {
            if (isNarrow)
            {
                SetNarrowStage(GetPreferredNarrowStage(preserveContext));
            }
            else
            {
                UpdateBreadcrumbs();
            }

            return;
        }

        UseNarrowLayout = isNarrow;

        if (isNarrow)
        {
            SetNarrowStage(GetPreferredNarrowStage(preserveContext));
        }
        else
        {
            UpdateBreadcrumbs();
        }
    }

    public void ShowArtistsRoot()
    {
        SelectedAlbumForTracks = null;
        SetNarrowStage(ArtistsLibraryStage.Artists);
    }

    public void ShowSelectedArtistDetails(LibraryArtistDto? artist = null)
    {
        if (artist != null)
        {
            SelectedArtist = artist;
        }

        if (SelectedArtist == null)
        {
            return;
        }

        SelectedAlbumForTracks = null;
        SetNarrowStage(ArtistsLibraryStage.Details);
    }

    public void ShowSelectedLikedArtistDetails(LikedArtistDto? artist = null)
    {
        if (artist != null)
        {
            SelectedLikedArtist = artist;
        }

        if (SelectedLikedArtist == null)
        {
            return;
        }

        SelectedAlbumForTracks = null;
        SetNarrowStage(ArtistsLibraryStage.Details);
    }

    public void ShowSelectedAlbumTracks(ArtistAlbumItemViewModel? album = null)
    {
        if (album != null)
        {
            SelectedAlbumForTracks = album;
        }

        if (SelectedAlbumForTracks == null)
        {
            return;
        }

        SetNarrowStage(ArtistsLibraryStage.Tracks);
    }

    private ArtistsLibraryStage GetPreferredNarrowStage(bool preserveContext)
    {
        if (!preserveContext)
        {
            return ArtistsLibraryStage.Artists;
        }

        if (SelectedAlbumForTracks != null)
        {
            return ArtistsLibraryStage.Tracks;
        }

        return (SelectedArtist != null || SelectedLikedArtist != null)
            ? ArtistsLibraryStage.Details
            : ArtistsLibraryStage.Artists;
    }

    private void SetNarrowStage(ArtistsLibraryStage stage)
    {
        NarrowStage = stage;
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbItems.Clear();
        BreadcrumbItems.Add(AppLocalization.GetString("Shell_SidebarArtists"));

        if (!UseNarrowLayout)
        {
            OnPropertyChanged(nameof(ShowBreadcrumbBar));
            return;
        }

        if (NarrowStage is ArtistsLibraryStage.Details or ArtistsLibraryStage.Tracks)
        {
            var artistName = SelectedArtist?.Name ?? SelectedLikedArtist?.Name;
            if (!string.IsNullOrEmpty(artistName))
                BreadcrumbItems.Add(artistName);
        }

        if (NarrowStage == ArtistsLibraryStage.Tracks && SelectedAlbumForTracks != null)
        {
            BreadcrumbItems.Add(SelectedAlbumForTracks.Album.Name);
        }

        OnPropertyChanged(nameof(ShowBreadcrumbBar));
    }

    private async Task LoadSelectedArtistDetailsAsync()
    {
        if (SelectedArtist == null)
        {
            _allAlbums.Clear();
            AlbumGroups.Clear();
            return;
        }

        try
        {
            IsLoadingDetails = true;

            // Fetch full discography plus the two local library sources that
            // mark an album as "in library": directly saved albums and albums
            // represented by liked songs.
            var discographyTask = _artistService.GetDiscographyAllAsync(SelectedArtist.Id, 0, 100);
            var savedAlbumsTask = _libraryDataService.GetAlbumsAsync();
            var likedSongsTask = _libraryDataService.GetLikedSongsAsync();

            await Task.WhenAll(discographyTask, savedAlbumsTask, likedSongsTask);

            _allAlbums = ArtistDiscographyLibraryMapper
                .Map(await discographyTask, await savedAlbumsTask, await likedSongsTask)
                .ToList();

            OnPropertyChanged(nameof(SavedAlbumCount));
            OnPropertyChanged(nameof(TotalAlbumCount));
            OnPropertyChanged(nameof(SavedOnlyButtonLabel));

            ApplyAlbumFilter();
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    partial void OnShowSavedOnlyChanged(bool value)
    {
        if (_savedOnlyAction is not null && _savedOnlyAction.IsChecked != value)
            _savedOnlyAction.IsChecked = value;
        ApplyAlbumFilter();
    }

    private void ApplyAlbumFilter()
    {
        var source = ShowSavedOnly ? _allAlbums.Where(a => a.IsInLibrary) : _allAlbums;

        var groups = new[]
        {
            ("Albums", "Album", source.Where(a => a.AlbumType is "ALBUM").ToList()),
            ("Singles & EPs", "Single,EP", source.Where(a => a.AlbumType is "SINGLE" or "EP").ToList()),
            ("Compilations", "Compilation", source.Where(a => a.AlbumType is "COMPILATION").ToList())
        };

        AlbumGroups.Clear();
        foreach (var (name, type, albumsList) in groups)
        {
            if (albumsList.Count > 0)
            {
                AlbumGroups.Add(new ArtistAlbumGroupViewModel(
                    name,
                    type,
                    albumsList,
                    _albumService,
                    _playbackService,
                    onAlbumSelected: album => SelectedAlbumForTracks = album));
            }
        }
    }


    private void OnSaveStateChanged()
    {
        if (_disposed)
            return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed)
                return;

            if (LikeService == null) return;

            // Remove artists that are no longer followed
            var removed = Artists.Where(a => !LikeService.IsSaved(SavedItemType.Artist, a.Id)).ToList();
            foreach (var artist in removed)
            {
                Artists.Remove(artist);
            }

            // Clear selection if the selected artist was removed
            if (SelectedArtist != null && removed.Any(a => a.Id == SelectedArtist.Id))
            {
                SelectedArtist = null;
            }

            // Check for newly followed artists not yet in our collection
            var existingIds = Artists.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var savedIds = LikeService.GetSavedIds(SavedItemType.Artist);
            var hasGhosts = Artists.Any(a => a.IsLoading);

            var newIds = savedIds
                .Select(bareId => $"spotify:artist:{bareId}")
                .Where(uri => !existingIds.Contains(uri))
                .ToList();

            if (newIds.Count > 0)
            {
                // Add ghost entries immediately for instant UI feedback
                foreach (var uri in newIds)
                {
                    Artists.Add(new LibraryArtistDto
                    {
                        Id = uri,
                        Name = "",
                        IsLoading = true,
                        AddedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            else if (hasGhosts)
            {
                // Ghost entries exist — try to resolve them from DB
                await LoadDataAsync(preserveSelection: true);
                return;
            }

            ApplyFilter();

            // Select first if nothing selected
            if (SourceMode == LibrarySource.Saved && SelectedArtist == null && FilteredArtists.Count > 0)
            {
                SelectedArtist = FilteredArtists[0];
            }

            if (LikedSideLoaded)
            {
                await LoadLikedArtistsAsync(preserveSelection: true);
            }
        });
    }

    private void ApplyFilter()
    {
        if (SourceMode == LibrarySource.Saved)
            ApplyFilterSaved();
        else
            ApplyFilterLiked();
    }

    private void ApplyFilterSaved()
    {
        var selectedId = SelectedArtist?.Id;

        var query = SearchQuery?.Trim() ?? "";
        IEnumerable<LibraryArtistDto> filtered = string.IsNullOrEmpty(query)
            ? Artists
            : Artists.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        var showRecents = SortBy == LibrarySortBy.Recents;
        var sorted = SortArtists(filtered).ToList();
        foreach (var artist in sorted)
        {
            artist.RecentsSubtitle = showRecents && _artistRecents.TryGetValue(artist.Id, out var ts)
                ? FormatRecentsSubtitle(ts)
                : null;
        }

        // Incremental, flicker-free apply — keeps row identity (selection / image / scroll).
        FilteredArtists.ApplyKeyedDiff(sorted, a => a.Id, keyComparer: StringComparer.OrdinalIgnoreCase);

        PreserveSelectedArtistAfterFilter(selectedId);
    }

    private void ApplyFilterLiked()
    {
        var selectedId = SelectedLikedArtist?.Id;

        var query = SearchQuery?.Trim() ?? "";
        IEnumerable<LikedArtistDto> filtered = string.IsNullOrEmpty(query)
            ? LikedArtists
            : LikedArtists.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        var showRecents = SortBy == LibrarySortBy.Recents;
        var sorted = SortLikedArtists(filtered).ToList();
        foreach (var artist in sorted)
        {
            artist.RecentsSubtitle = showRecents && _artistRecents.TryGetValue(artist.Id, out var ts)
                ? FormatRecentsSubtitle(ts)
                : null;
        }

        FilteredLikedArtists.ApplyKeyedDiff(sorted, a => a.Id, keyComparer: StringComparer.OrdinalIgnoreCase);

        PreserveSelectedLikedArtistAfterFilter(selectedId);
    }

    private void PreserveSelectedArtistAfterFilter(string? selectedId)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        var selected = FilteredArtists.FirstOrDefault(a =>
            string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected != null && !ReferenceEquals(SelectedArtist, selected))
        {
            SelectedArtist = selected;
        }
        else if (selected == null && string.Equals(SelectedArtist?.Id, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedArtist = null;
        }
    }

    private void PreserveSelectedLikedArtistAfterFilter(string? selectedId)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        var selected = FilteredLikedArtists.FirstOrDefault(a =>
            string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected != null && !ReferenceEquals(SelectedLikedArtist, selected))
        {
            SelectedLikedArtist = selected;
        }
        else if (selected == null && string.Equals(SelectedLikedArtist?.Id, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLikedArtist = null;
        }
    }

    private IEnumerable<LibraryArtistDto> SortArtists(IEnumerable<LibraryArtistDto> source)
    {
        var descending = SortDirection == LibrarySortDirection.Descending;

        return SortBy switch
        {
            // Recents uses real play recency (LibraryRecentsService); never-played artists
            // fall to the bottom (desc) or top (asc) via DateTimeOffset.MinValue. Ties break
            // by AddedAt desc for a stable order.
            LibrarySortBy.Recents => descending
                ? source.OrderByDescending(a => LastPlayedOrMin(a)).ThenByDescending(a => a.AddedAt)
                : source.OrderBy(a => LastPlayedOrMin(a)).ThenByDescending(a => a.AddedAt),
            LibrarySortBy.RecentlyAdded => descending
                ? source.OrderByDescending(a => a.AddedAt)
                : source.OrderBy(a => a.AddedAt),
            LibrarySortBy.Alphabetical => descending
                ? source.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            // Creator / ReleaseDate aren't offered for artists; fall through to RecentlyAdded.
            _ => descending
                ? source.OrderByDescending(a => a.AddedAt)
                : source.OrderBy(a => a.AddedAt)
        };
    }

    private IEnumerable<LikedArtistDto> SortLikedArtists(IEnumerable<LikedArtistDto> source)
    {
        var descending = SortDirection == LibrarySortDirection.Descending;

        return SortBy switch
        {
            LibrarySortBy.Recents => descending
                ? source.OrderByDescending(a => LastPlayedOrMin(a.Id)).ThenByDescending(a => a.MostRecentLikedAt)
                : source.OrderBy(a => LastPlayedOrMin(a.Id)).ThenByDescending(a => a.MostRecentLikedAt),
            LibrarySortBy.RecentlyAdded => descending
                ? source.OrderByDescending(a => a.MostRecentLikedAt)
                : source.OrderBy(a => a.MostRecentLikedAt),
            LibrarySortBy.Alphabetical => descending
                ? source.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? source.OrderByDescending(a => a.MostRecentLikedAt)
                : source.OrderBy(a => a.MostRecentLikedAt)
        };
    }

    private DateTimeOffset LastPlayedOrMin(LibraryArtistDto artist) =>
        _artistRecents.TryGetValue(artist.Id, out var ts) ? ts : DateTimeOffset.MinValue;

    private DateTimeOffset LastPlayedOrMin(string artistId) =>
        _artistRecents.TryGetValue(artistId, out var ts) ? ts : DateTimeOffset.MinValue;

    #region ITrackListViewModel Implementation

    // Selection tracking — SelectedItems / SelectedCount / HasSelection /
    // SelectionHeaderText are inherited from TrackListViewModelBase.

    // Sorting - no-op for album tracks (always in track order)
    // Renamed from SortBy to avoid colliding with the LibrarySortBy observable
    // property that drives the library grid's global sort.
    [RelayCommand]
    private void SortTrackColumn(string? columnName) { }

    public string SortChevronGlyph => "";
    public bool IsSortingByTitle => false;
    public bool IsSortingByArtist => false;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    private static string? GetTrackUri(object? item) =>
        item is AlbumTrackDto albumTrack ? albumTrack.Uri :
        item is LikedSongDto likedSong ? likedSong.Uri :
        null;

    // Playback commands
    [RelayCommand]
    private async Task PlayTrackAsync(object? track)
    {
        if (track is AlbumTrackDto albumTrack)
        {
            var albumId = SelectedAlbumForTracks?.Album?.Id;
            if (albumId != null)
                await _playbackService.PlayTrackInContextAsync(albumTrack.Uri, albumId);
            else
                await _playbackService.PlayTracksAsync([albumTrack.Uri]);
            return;
        }

        if (track is LikedSongDto likedSong && SelectedLikedArtist is { } likedArtist)
        {
            var trackUris = likedArtist.LikedSongs.Select(s => s.Uri).ToList();
            if (trackUris.Count == 0) return;

            var startIndex = trackUris.IndexOf(likedSong.Uri);
            if (startIndex < 0) startIndex = 0;

            var contextInfo = new PlaybackContextInfo
            {
                ContextUri = likedArtist.CanOpenArtist ? likedArtist.Id : "spotify:collection",
                Type = PlaybackContextType.Artist,
                Name = likedArtist.Name,
                ImageUrl = likedArtist.ImageUrl,
            };
            await _playbackService.PlayTracksAsync(trackUris, startIndex, contextInfo);
        }
    }

    // Multi-select commands
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlaySelectedAsync()
    {
        if (!HasSelection) return;
        var trackUris = SelectedItems
            .Select(GetTrackUri)
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => u!)
            .ToList();
        if (trackUris.Count > 0)
            await _playbackService.PlayTracksAsync(trackUris);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlayAfterAsync()
    {
        if (!HasSelection) return;
        foreach (var item in SelectedItems)
        {
            var uri = GetTrackUri(item);
            if (!string.IsNullOrEmpty(uri))
                await _playbackService.PlayNextAsync(uri);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddSelectedToQueueAsync()
    {
        if (!HasSelection) return;
        foreach (var item in SelectedItems)
        {
            var uri = GetTrackUri(item);
            if (!string.IsNullOrEmpty(uri))
                await _playbackService.AddToQueueAsync(uri);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (!HasSelection || LikeService == null) return;
        foreach (var item in SelectedItems)
        {
            var uri = GetTrackUri(item);
            if (string.IsNullOrEmpty(uri)) continue;

            // Force currentlySaved=true so the toggle always lands on "unsaved" —
            // matches the menu label "Remove from library".
            LikeService.ToggleSave(SavedItemType.Track, uri, currentlySaved: true);
        }
    }

    // Explicit ITrackListViewModel ICommand implementation
    ICommand ITrackListViewModel.SortByCommand => SortTrackColumnCommand;
    ICommand ITrackListViewModel.PlayTrackCommand => PlayTrackCommand;
    ICommand ITrackListViewModel.PlaySelectedCommand => PlaySelectedCommand;
    ICommand ITrackListViewModel.PlayAfterCommand => PlayAfterCommand;
    ICommand ITrackListViewModel.AddSelectedToQueueCommand => AddSelectedToQueueCommand;
    ICommand ITrackListViewModel.RemoveSelectedCommand => RemoveSelectedCommand;

    #endregion

    protected override void OnSelectionChanged()
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlayAfterCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    public void UnlikeAllSongsFromLikedArtist(LikedArtistDto artist)
    {
        if (artist == null || LikeService == null) return;
        foreach (var track in artist.LikedSongs)
        {
            if (string.IsNullOrEmpty(track.Uri)) continue;
            LikeService.ToggleSave(SavedItemType.Track, track.Uri, currentlySaved: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DetachLongLivedServices();
    }
}
