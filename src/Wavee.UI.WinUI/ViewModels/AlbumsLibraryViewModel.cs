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
using Wavee.UI.Models;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Controls.Library;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public enum AlbumsLibraryStage
{
    Grid,
    Details
}

/// <summary>Which tracklist the From-Liked-Songs detail pane is showing.</summary>
public enum LikedAlbumDetailMode
{
    /// <summary>Only the liked tracks of the album.</summary>
    Liked,
    /// <summary>The full album tracklist (lazily fetched).</summary>
    FullAlbum
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class AlbumsLibraryViewModel : DualSourceLibraryViewModelBase<LibraryAlbumDto, LikedAlbumDto>, ITrackListViewModel, IDisposable
{
    protected override string SavedPreferencesKey => "albums.saved";
    protected override string LikedPreferencesKey => "albums.liked";

    private readonly ILibraryDataService _libraryDataService;
    private readonly IAlbumService _albumService;
    private readonly IPlaybackService _playbackService;
    private readonly ILikedSongsGroupingCache _grouping;
    private bool _disposed;
    private IReadOnlyDictionary<string, DateTimeOffset> _albumRecents =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial bool IsLoadingTracks { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<LibraryAlbumDto> Albums { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<LibraryAlbumDto> FilteredAlbums { get; set; } = [];

    [ObservableProperty]
    public partial LibraryAlbumDto? SelectedAlbum { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<AlbumTrackDto> SelectedAlbumTracks { get; set; } = [];

    [ObservableProperty]
    public partial TimeSpan SelectedAlbumDuration { get; set; }

    // Wrapper properties for selected album (avoids null reference in x:Bind).
    // Set from EITHER SelectedAlbum (Saved source) OR SelectedLikedAlbum
    // (From-Liked-Songs source) so the detail-pane hero stays unified.
    [ObservableProperty]
    public partial string SelectedAlbumName { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedAlbumArtist { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedAlbumYear { get; set; }

    [ObservableProperty]
    public partial int SelectedAlbumTrackCount { get; set; }

    [ObservableProperty]
    public partial string? SelectedAlbumImageUrl { get; set; }

    [ObservableProperty]
    public partial string SelectedAlbumMetadata { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWideLayout))]
    [NotifyPropertyChangedFor(nameof(IsNarrowLayout))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowGridStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    [NotifyPropertyChangedFor(nameof(IsSavedWide))]
    [NotifyPropertyChangedFor(nameof(IsLikedWide))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowGrid))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowGrid))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowDetails))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowDetails))]
    public partial bool UseNarrowLayout { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNarrowGridStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowGrid))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowGrid))]
    [NotifyPropertyChangedFor(nameof(ShowSavedNarrowDetails))]
    [NotifyPropertyChangedFor(nameof(ShowLikedNarrowDetails))]
    public partial AlbumsLibraryStage NarrowStage { get; set; } = AlbumsLibraryStage.Grid;

    // ── From-Liked-Songs source ──

    [ObservableProperty]
    public partial ObservableCollection<LikedAlbumDto> LikedAlbums { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<LikedAlbumDto> FilteredLikedAlbums { get; set; } = [];

    [ObservableProperty]
    public partial LikedAlbumDto? SelectedLikedAlbum { get; set; }

    /// <summary>Liked-tracks subset of the currently selected liked-album, in original liked-songs order.</summary>
    [ObservableProperty]
    public partial ObservableCollection<LikedSongDto> SelectedLikedAlbumLikedTracks { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLikedTracksInDetail))]
    [NotifyPropertyChangedFor(nameof(ShowFullAlbumTracksInDetail))]
    [NotifyPropertyChangedFor(nameof(IsLikedDetailTabActive))]
    [NotifyPropertyChangedFor(nameof(IsFullAlbumDetailTabActive))]
    public partial LikedAlbumDetailMode LikedAlbumDetailMode { get; set; } = LikedAlbumDetailMode.Liked;

    public ObservableCollection<string> BreadcrumbItems { get; } = [];

    /// <summary>Declarative action row for the shared <c>LibraryDetailPanel</c> — Saved source.</summary>
    public ObservableCollection<LibraryDetailAction> SavedDetailActions { get; } = [];

    /// <summary>Declarative action row for the shared <c>LibraryDetailPanel</c> — From-Liked-Songs source.</summary>
    public ObservableCollection<LibraryDetailAction> LikedDetailActions { get; } = [];

    public bool IsWideLayout => !UseNarrowLayout;
    public bool IsNarrowLayout => UseNarrowLayout;
    public bool ShowNarrowGridStage => UseNarrowLayout && NarrowStage == AlbumsLibraryStage.Grid;
    public bool ShowNarrowDetailsStage => UseNarrowLayout && NarrowStage == AlbumsLibraryStage.Details;
    public bool ShowBreadcrumbBar => UseNarrowLayout;

    /// <summary>True while showing the liked-tracks-only subset in the From-Liked-Songs detail pane.</summary>
    public bool ShowLikedTracksInDetail =>
        SourceMode == LibrarySource.FromLikedSongs && LikedAlbumDetailMode == LikedAlbumDetailMode.Liked;

    /// <summary>
    /// True while the full album tracklist should be visible in the detail pane —
    /// either because we're on the Saved source (always) or the user toggled
    /// "Full album" on the From-Liked-Songs detail.
    /// </summary>
    public bool ShowFullAlbumTracksInDetail =>
        SourceMode == LibrarySource.Saved || LikedAlbumDetailMode == LikedAlbumDetailMode.FullAlbum;

    public bool IsLikedDetailTabActive => LikedAlbumDetailMode == LikedAlbumDetailMode.Liked;
    public bool IsFullAlbumDetailTabActive => LikedAlbumDetailMode == LikedAlbumDetailMode.FullAlbum;

    // Visibility helpers — pre-computed AND expressions so the XAML can use
    // single-binding BoolToVisibility converters instead of multi-binding.
    public bool IsSavedWide => IsSavedSource && IsWideLayout;
    public bool IsLikedWide => IsLikedSource && IsWideLayout;
    public bool ShowSavedNarrowGrid => IsSavedSource && ShowNarrowGridStage;
    public bool ShowLikedNarrowGrid => IsLikedSource && ShowNarrowGridStage;
    public bool ShowSavedNarrowDetails => IsSavedSource && ShowNarrowDetailsStage;
    public bool ShowLikedNarrowDetails => IsLikedSource && ShowNarrowDetailsStage;

    public AlbumsLibraryViewModel(
        ILibraryDataService libraryDataService,
        IAlbumService albumService,
        IPlaybackService playbackService,
        ILikedSongsGroupingCache grouping,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        LibraryRecentsService? libraryRecents = null)
        : base(settingsService, likeService, libraryRecents, DispatcherQueue.GetForCurrentThread())
    {
        _libraryDataService = libraryDataService;
        _albumService = albumService;
        _playbackService = playbackService;
        _grouping = grouping;

        LoadPreferences();
        BuildDetailActions();

        AttachLongLivedServices();
        if (LibraryRecents != null)
            // Best-effort prefetch; result arrives via RecentsChanged → re-applies sort.
            _ = PrefetchRecentsAsync();
    }

    private void BuildDetailActions()
    {
        SavedDetailActions.Add(new LibraryDetailAction { Label = "Play", Glyph = FluentGlyphs.Play, IsAccent = true, Command = PlayAlbumCommand });
        SavedDetailActions.Add(new LibraryDetailAction { Label = "Shuffle", Glyph = FluentGlyphs.Shuffle, Command = ShuffleAlbumCommand });
        SavedDetailActions.Add(new LibraryDetailAction { Label = "View album", Glyph = FluentGlyphs.ShowFilled, Command = OpenAlbumDetailsCommand });
        SavedDetailActions.Add(new LibraryDetailAction { Label = "Unheart", Glyph = FluentGlyphs.HeartFilled, Command = UnheartSelectedAlbumCommand });

        LikedDetailActions.Add(new LibraryDetailAction { Label = "Play liked", Glyph = FluentGlyphs.Play, IsAccent = true, Command = PlayLikedAlbumLikedTracksCommand, Tooltip = "Play the liked tracks of this album" });
        LikedDetailActions.Add(new LibraryDetailAction { Label = "Shuffle", Glyph = FluentGlyphs.Shuffle, Command = ShuffleLikedAlbumLikedTracksCommand });
        LikedDetailActions.Add(new LibraryDetailAction { Label = "Open album", Glyph = FluentGlyphs.Open, Command = OpenAlbumDetailsCommand });
    }

    private async Task PrefetchRecentsAsync()
    {
        if (LibraryRecents == null) return;
        try
        {
            var map = await LibraryRecents.GetAlbumRecentsAsync().ConfigureAwait(false);
            _albumRecents = map;
            DispatcherQueue.TryEnqueue(ApplyFilter);
        }
        catch
        {
            // Swallow — sort falls back to AddedAt.
        }
    }

    private void OnLibraryRecentsChanged()
    {
        if (_disposed || LibraryRecents == null) return;
        // The service raises on the UI dispatcher, but await the refetch off the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var map = await LibraryRecents.GetAlbumRecentsAsync().ConfigureAwait(false);
                _albumRecents = map;
                DispatcherQueue.TryEnqueue(() =>
                {
                    ApplyFilter();
                    // Detail-panel metadata embeds the last-played line; refresh it so the
                    // currently-selected album picks up the timestamp without reselection.
                    if (SelectedAlbum is { } current)
                        SelectedAlbumMetadata = BuildSelectedAlbumMetadata(current);
                });
            }
            catch { /* ignore */ }
        });
    }

    protected override LibrarySource ReadPersistedSource(AppSettings settings) =>
        string.Equals(settings.AlbumsLibrarySource, nameof(LibrarySource.FromLikedSongs), StringComparison.OrdinalIgnoreCase)
            ? LibrarySource.FromLikedSongs
            : LibrarySource.Saved;

    protected override void WritePersistedSource(AppSettings settings, LibrarySource source)
    {
        settings.AlbumsLibrarySource = source.ToString();
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
        OnPropertyChanged(nameof(ShowLikedTracksInDetail));
        OnPropertyChanged(nameof(ShowFullAlbumTracksInDetail));
        OnPropertyChanged(nameof(IsSavedWide));
        OnPropertyChanged(nameof(IsLikedWide));
        OnPropertyChanged(nameof(ShowSavedNarrowGrid));
        OnPropertyChanged(nameof(ShowLikedNarrowGrid));
        OnPropertyChanged(nameof(ShowSavedNarrowDetails));
        OnPropertyChanged(nameof(ShowLikedNarrowDetails));

        // Clear the cross-source selection so the detail pane resets cleanly.
        if (newValue == LibrarySource.Saved)
        {
            SelectedLikedAlbum = null;
            if (SelectedAlbum == null && FilteredAlbums.Count > 0)
                SelectedAlbum = FilteredAlbums[0];
        }
        else
        {
            SelectedAlbum = null;
            // Lazy-load liked-albums on first switch.
            if (!LikedSideLoaded)
                _ = LoadLikedAlbumsAsync(preserveSelection: false);
            else
            {
                ApplyFilter();
                if (SelectedLikedAlbum == null && FilteredLikedAlbums.Count > 0)
                    SelectedLikedAlbum = FilteredLikedAlbums[0];
            }
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Skip if already loaded (for page cache restoration)
        if (IsLoading || Albums.Count > 0) return;

        await LoadDataAsync(preserveSelection: false);

        // If the persisted source is already From-Liked-Songs, also pull
        // the liked side on first load so the user lands in a populated grid.
        if (SourceMode == LibrarySource.FromLikedSongs && !LikedSideLoaded)
        {
            await LoadLikedAlbumsAsync(preserveSelection: false);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        await LoadDataAsync(preserveSelection: true);
        if (LikedSideLoaded)
            await LoadLikedAlbumsAsync(preserveSelection: true);
    }

    private async Task LoadDataAsync(bool preserveSelection)
    {
        var previousSelectedId = preserveSelection ? SelectedAlbum?.Id : null;

        try
        {
            IsLoading = true;

            // Load albums
            var albums = await _libraryDataService.GetAlbumsAsync();

            Albums.Clear();
            foreach (var album in albums)
            {
                Albums.Add(album);
            }

            ApplyFilter();

            // Restore previous selection or select first
            if (previousSelectedId != null)
            {
                SelectedAlbum = FilteredAlbums.FirstOrDefault(a => a.Id == previousSelectedId);
            }

            if (SourceMode == LibrarySource.Saved && SelectedAlbum == null && FilteredAlbums.Count > 0)
            {
                SelectedAlbum = FilteredAlbums[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Pulls the user's liked songs, groups them by parent album via
    /// <see cref="LikedSongsByAlbumGrouper"/>, and refreshes the
    /// From-Liked-Songs grid. Stamps <see cref="LikedAlbumDto.IsAlsoSaved"/>
    /// from the current saved-albums set.
    /// </summary>
    private async Task LoadLikedAlbumsAsync(bool preserveSelection)
    {
        var previousSelectedId = preserveSelection ? SelectedLikedAlbum?.Id : null;

        try
        {
            IsLoading = true;
            // Shared cache: one liked-songs fetch + one grouping reused across the
            // Albums and Artists tabs; rebuilt only on save-state change.
            var grouped = await _grouping.GetAlbumsAsync();

            LikedAlbums.ApplyKeyedDiff(grouped, a => a.Id, keyComparer: StringComparer.OrdinalIgnoreCase);

            LikedSideLoaded = true;
            ApplyFilter();

            if (SourceMode == LibrarySource.FromLikedSongs)
            {
                if (previousSelectedId != null)
                    SelectedLikedAlbum = FilteredLikedAlbums.FirstOrDefault(a =>
                        string.Equals(a.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase));

                if (SelectedLikedAlbum == null && FilteredLikedAlbums.Count > 0)
                    SelectedLikedAlbum = FilteredLikedAlbums[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayAlbumAsync()
    {
        if (SelectedAlbum == null) return;
        await _playbackService.PlayContextAsync(
            SelectedAlbum.Id,
            new PlayContextOptions { PlayOriginFeature = "album_library" });
    }

    [RelayCommand]
    private async Task ShuffleAlbumAsync()
    {
        if (SelectedAlbum == null) return;
        await _playbackService.PlayContextAsync(
            SelectedAlbum.Id,
            new PlayContextOptions { Shuffle = true, PlayOriginFeature = "album_library" });
    }

    [RelayCommand]
    private void UnheartSelectedAlbum()
    {
        if (SelectedAlbum is { } album)
            UnheartAlbum(album);
    }

    [RelayCommand]
    private async Task PlayTrackAsync(object? track)
    {
        if (track is AlbumTrackDto albumTrack)
        {
            // Saved source OR From-Liked-Songs "Full album" tab. Context = the
            // album, so the player walks the full tracklist.
            var contextUri = SelectedAlbum?.Id ?? SelectedLikedAlbum?.Id;
            if (contextUri == null) return;
            await _playbackService.PlayTrackInContextAsync(albumTrack.Uri, contextUri);
            return;
        }

        if (track is LikedSongDto liked && SelectedLikedAlbum is { } likedAlbum)
        {
            // From-Liked-Songs "Liked" tab. Plays the liked subset only,
            // anchored to this track. Context metadata uses the album so
            // remote Spotify clients display "Playing from {album}".
            var trackUris = likedAlbum.LikedSongs.Select(s => s.Uri).ToList();
            if (trackUris.Count == 0) return;

            var startIndex = trackUris.IndexOf(liked.Uri);
            if (startIndex < 0) startIndex = 0;

            var contextInfo = new PlaybackContextInfo
            {
                ContextUri = likedAlbum.Id,
                Type = PlaybackContextType.Album,
                Name = likedAlbum.Name,
                ImageUrl = likedAlbum.ImageUrl,
            };

            await _playbackService.PlayTracksAsync(trackUris, startIndex, contextInfo);
        }
    }

    /// <summary>Plays the liked-tracks subset of the currently selected liked-album.</summary>
    [RelayCommand]
    private async Task PlayLikedAlbumLikedTracksAsync()
    {
        if (SelectedLikedAlbum is not { } liked) return;
        var trackUris = liked.LikedSongs.Select(s => s.Uri).ToList();
        if (trackUris.Count == 0) return;

        var contextInfo = new PlaybackContextInfo
        {
            ContextUri = liked.Id,
            Type = PlaybackContextType.Album,
            Name = liked.Name,
            ImageUrl = liked.ImageUrl,
        };
        await _playbackService.PlayTracksAsync(trackUris, 0, contextInfo);
    }

    /// <summary>Shuffle-plays the liked-tracks subset of the currently selected liked-album.</summary>
    [RelayCommand]
    private async Task ShuffleLikedAlbumLikedTracksAsync()
    {
        if (SelectedLikedAlbum is not { } liked) return;
        var trackUris = liked.LikedSongs.Select(s => s.Uri).ToList();
        if (trackUris.Count == 0) return;

        // Fisher-Yates shuffle on a copy so PlayTracksAsync starts at the
        // first shuffled position; we don't toggle the player's repeat/shuffle
        // mode because that would persist across the next play action too.
        var rng = Random.Shared;
        for (var i = trackUris.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (trackUris[i], trackUris[j]) = (trackUris[j], trackUris[i]);
        }

        var contextInfo = new PlaybackContextInfo
        {
            ContextUri = liked.Id,
            Type = PlaybackContextType.Album,
            Name = liked.Name,
            ImageUrl = liked.ImageUrl,
        };
        await _playbackService.PlayTracksAsync(trackUris, 0, contextInfo);
    }

    [RelayCommand]
    private void OpenAlbumDetails()
    {
        if (SelectedAlbum is { } saved)
        {
            // Pass the lean library data via ContentNavigationParameter so AlbumPage
            // can PrefillFrom(...) and render the hero (cover + name + artist) in
            // the first frame, without waiting for the AlbumStore Pathfinder fetch.
            Helpers.Navigation.NavigationHelpers.OpenAlbum(
                new Data.Parameters.ContentNavigationParameter
                {
                    Uri = saved.Id,
                    Title = saved.Name,
                    Subtitle = saved.ArtistName,
                    ImageUrl = saved.ImageUrl,
                },
                saved.Name);
            return;
        }

        if (SelectedLikedAlbum is { } liked)
        {
            Helpers.Navigation.NavigationHelpers.OpenAlbum(
                new Data.Parameters.ContentNavigationParameter
                {
                    Uri = liked.Id,
                    Title = liked.Name,
                    Subtitle = liked.ArtistName,
                    ImageUrl = liked.ImageUrl,
                },
                liked.Name);
        }
    }

    [RelayCommand]
    private void OpenArtist()
    {
        var artistId = SelectedAlbum?.ArtistId ?? SelectedLikedAlbum?.ArtistId;
        var artistName = SelectedAlbum?.ArtistName ?? SelectedLikedAlbum?.ArtistName ?? "";
        if (string.IsNullOrEmpty(artistId)) return;

        // ArtistName ships as the title; the library album row doesn't carry a
        // separate artist image, so ImageUrl is left null and ArtistPage falls
        // back to the avatar URL once ArtistStore returns.
        Helpers.Navigation.NavigationHelpers.OpenArtist(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = artistId,
                Title = artistName,
            },
            artistName);
    }

    [RelayCommand]
    private void ShowLikedTab()
    {
        LikedAlbumDetailMode = LikedAlbumDetailMode.Liked;
    }

    [RelayCommand]
    private void ShowFullAlbumTab()
    {
        LikedAlbumDetailMode = LikedAlbumDetailMode.FullAlbum;
    }

    partial void OnSelectedAlbumChanged(LibraryAlbumDto? value)
    {
        if (value != null)
        {
            // Selecting from the Saved grid implicitly clears the liked-side
            // selection so the unified wrapper props don't fight each other.
            if (SelectedLikedAlbum != null)
                SelectedLikedAlbum = null;
        }

        // Update wrapper properties from the Saved-source DTO.
        SelectedAlbumName = value?.Name ?? "";
        SelectedAlbumArtist = value?.ArtistName ?? "";
        SelectedAlbumYear = value?.Year ?? 0;
        SelectedAlbumTrackCount = value?.TrackCount ?? 0;
        SelectedAlbumImageUrl = value?.ImageUrl;
        SelectedAlbumMetadata = value != null
            ? BuildSelectedAlbumMetadata(value)
            : "";

        if (UseNarrowLayout && value == null && SelectedLikedAlbum == null)
        {
            NarrowStage = AlbumsLibraryStage.Grid;
        }

        UpdateBreadcrumbs();

        if (value != null)
            _ = LoadSelectedAlbumTracksAsync();
    }

    partial void OnSelectedLikedAlbumChanged(LikedAlbumDto? value)
    {
        if (value != null && SelectedAlbum != null)
            SelectedAlbum = null;

        // Reset the detail-tab mode whenever a new liked-album is selected so
        // the user always lands on the "Liked" subset first.
        LikedAlbumDetailMode = LikedAlbumDetailMode.Liked;

        // Drive the unified wrapper props from the liked-side DTO.
        SelectedAlbumName = value?.Name ?? "";
        SelectedAlbumArtist = value?.ArtistName ?? "";
        SelectedAlbumYear = value?.Year ?? 0;
        SelectedAlbumTrackCount = value?.TrackCount ?? 0;
        SelectedAlbumImageUrl = value?.ImageUrl;
        SelectedAlbumMetadata = value != null
            ? BuildSelectedLikedAlbumMetadata(value)
            : "";

        // Push the liked-tracks subset into the detail pane.
        SelectedLikedAlbumLikedTracks.Clear();
        if (value != null)
        {
            foreach (var t in value.LikedSongs)
                SelectedLikedAlbumLikedTracks.Add(t);
            SelectedAlbumDuration = TimeSpan.FromTicks(value.LikedSongs.Sum(t => t.Duration.Ticks));
        }
        else
        {
            SelectedAlbumDuration = TimeSpan.Zero;
        }

        // Clear the full-album tracklist; lazy-load only if/when the user flips
        // to the Full album tab.
        SelectedAlbumTracks.Clear();

        if (UseNarrowLayout && value == null && SelectedAlbum == null)
            NarrowStage = AlbumsLibraryStage.Grid;

        UpdateBreadcrumbs();
    }

    partial void OnLikedAlbumDetailModeChanged(LikedAlbumDetailMode value)
    {
        if (value == LikedAlbumDetailMode.FullAlbum && SelectedLikedAlbum is { } liked && SelectedAlbumTracks.Count == 0)
        {
            // Lazy-load the full album tracklist on first activation.
            _ = LoadFullAlbumTracksForLikedAsync(liked.Id);
        }
    }

    private async Task LoadFullAlbumTracksForLikedAsync(string albumId)
    {
        try
        {
            IsLoadingTracks = true;
            var tracks = await _albumService.GetTracksAsync(albumId);

            // Tag the tracks that are in the user's Liked Songs so the
            // tracklist's heart marker stays visible across the Full album tab.
            HashSet<string>? likedUris = null;
            if (SelectedLikedAlbum is { } liked)
                likedUris = new HashSet<string>(liked.LikedSongs.Select(s => s.Uri), StringComparer.OrdinalIgnoreCase);

            SelectedAlbumTracks.Clear();
            var total = TimeSpan.Zero;
            foreach (var track in tracks)
            {
                if (likedUris != null && likedUris.Contains(track.Uri))
                    track.IsLiked = true;
                SelectedAlbumTracks.Add(track);
                total += track.Duration;
            }
            // Detail-pane duration shows the full album sum while the Full tab is active.
            if (LikedAlbumDetailMode == LikedAlbumDetailMode.FullAlbum)
                SelectedAlbumDuration = total;
        }
        finally
        {
            IsLoadingTracks = false;
        }
    }

    public void SetNarrowLayout(bool isNarrow, bool preserveContext)
    {
        var hasSelection = SelectedAlbum != null || SelectedLikedAlbum != null;
        if (UseNarrowLayout == isNarrow)
        {
            if (isNarrow)
            {
                SetNarrowStage(preserveContext && hasSelection
                    ? AlbumsLibraryStage.Details
                    : AlbumsLibraryStage.Grid);
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
            SetNarrowStage(preserveContext && hasSelection
                ? AlbumsLibraryStage.Details
                : AlbumsLibraryStage.Grid);
        }
        else
        {
            UpdateBreadcrumbs();
        }
    }

    public void ShowAlbumsRoot()
    {
        SetNarrowStage(AlbumsLibraryStage.Grid);
    }

    public void ShowSelectedAlbumDetails(LibraryAlbumDto? album = null)
    {
        if (album != null)
            SelectedAlbum = album;

        if (SelectedAlbum == null)
            return;

        SetNarrowStage(AlbumsLibraryStage.Details);
    }

    public void ShowSelectedLikedAlbumDetails(LikedAlbumDto? album = null)
    {
        if (album != null)
            SelectedLikedAlbum = album;

        if (SelectedLikedAlbum == null)
            return;

        SetNarrowStage(AlbumsLibraryStage.Details);
    }

    private void SetNarrowStage(AlbumsLibraryStage stage)
    {
        NarrowStage = stage;
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbItems.Clear();
        BreadcrumbItems.Add(AppLocalization.GetString("Shell_SidebarAlbums"));

        if (UseNarrowLayout && NarrowStage == AlbumsLibraryStage.Details)
        {
            var name = SelectedAlbum?.Name ?? SelectedLikedAlbum?.Name;
            if (!string.IsNullOrEmpty(name))
                BreadcrumbItems.Add(name);
        }

        OnPropertyChanged(nameof(ShowBreadcrumbBar));
    }

    private async Task LoadSelectedAlbumTracksAsync()
    {
        if (SelectedAlbum == null)
        {
            SelectedAlbumTracks.Clear();
            SelectedAlbumDuration = TimeSpan.Zero;
            return;
        }

        try
        {
            IsLoadingTracks = true;
            var tracks = await _albumService.GetTracksAsync(SelectedAlbum.Id);

            SelectedAlbumTracks.Clear();
            var totalDuration = TimeSpan.Zero;
            foreach (var track in tracks)
            {
                SelectedAlbumTracks.Add(track);
                totalDuration += track.Duration;
            }
            SelectedAlbumDuration = totalDuration;
        }
        finally
        {
            IsLoadingTracks = false;
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

            using var _p = Services.UiOperationProfiler.Instance?.Profile("AlbumLibrarySyncUI");
            if (LikeService == null) return;

            // ── Saved albums incremental refresh ──
            var removed = Albums.Where(a => !LikeService.IsSaved(SavedItemType.Album, a.Id)).ToList();
            foreach (var album in removed)
            {
                Albums.Remove(album);
            }

            if (SelectedAlbum != null && removed.Any(a => a.Id == SelectedAlbum.Id))
            {
                SelectedAlbum = null;
            }

            // Check for newly saved albums not yet in our collection
            var existingIds = Albums.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var savedIds = LikeService.GetSavedIds(SavedItemType.Album);
            var hasGhosts = Albums.Any(a => a.IsLoading);

            var newIds = savedIds
                .Select(bareId => $"spotify:album:{bareId}")
                .Where(uri => !existingIds.Contains(uri))
                .ToList();

            if (newIds.Count > 0)
            {
                // Add ghost entries immediately for instant UI feedback
                foreach (var uri in newIds)
                {
                    Albums.Add(new LibraryAlbumDto
                    {
                        Id = uri,
                        Name = "",
                        ArtistName = "",
                        IsLoading = true,
                        AddedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            else if (hasGhosts)
            {
                // Ghost entries exist — try to resolve them from DB
                await LoadDataAsync(preserveSelection: true);
            }

            ApplyFilter();

            if (SourceMode == LibrarySource.Saved && SelectedAlbum == null && FilteredAlbums.Count > 0)
            {
                SelectedAlbum = FilteredAlbums[0];
            }

            // ── From-Liked-Songs incremental refresh ──
            // Track likes change frequently relative to album likes; refresh
            // the grouped view from the underlying SQLite store. Only when
            // the liked side has been materialised at least once.
            if (LikedSideLoaded)
            {
                await LoadLikedAlbumsAsync(preserveSelection: true);
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
        var selectedId = SelectedAlbum?.Id;

        var query = SearchQuery?.Trim() ?? "";
        IEnumerable<LibraryAlbumDto> filtered = string.IsNullOrEmpty(query)
            ? Albums
            : Albums.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase));

        // When sorted by Recents, stamp each DTO with a "Played X ago" subtitle so the
        // list/grid templates can show it in place of the artist / added-date line.
        var showRecents = SortBy == LibrarySortBy.Recents;
        var sorted = SortAlbums(filtered).ToList();
        foreach (var album in sorted)
        {
            album.RecentsSubtitle = showRecents && _albumRecents.TryGetValue(album.Id, out var ts)
                ? FormatRecentsSubtitle(ts)
                : null;
        }

        // Incremental, flicker-free apply — keeps row identity (selection / image / scroll).
        FilteredAlbums.ApplyKeyedDiff(sorted, a => a.Id, keyComparer: StringComparer.OrdinalIgnoreCase);

        PreserveSelectedAlbumAfterFilter(selectedId);
    }

    private void ApplyFilterLiked()
    {
        var selectedId = SelectedLikedAlbum?.Id;

        var query = SearchQuery?.Trim() ?? "";
        IEnumerable<LikedAlbumDto> filtered = string.IsNullOrEmpty(query)
            ? LikedAlbums
            : LikedAlbums.Where(a =>
                (a.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.ArtistName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        var showRecents = SortBy == LibrarySortBy.Recents;
        var sorted = SortLikedAlbums(filtered).ToList();
        foreach (var album in sorted)
        {
            album.RecentsSubtitle = showRecents && _albumRecents.TryGetValue(album.Id, out var ts)
                ? FormatRecentsSubtitle(ts)
                : null;
        }

        FilteredLikedAlbums.ApplyKeyedDiff(sorted, a => a.Id, keyComparer: StringComparer.OrdinalIgnoreCase);

        PreserveSelectedLikedAlbumAfterFilter(selectedId);
    }

    private void PreserveSelectedAlbumAfterFilter(string? selectedId)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        var selected = FilteredAlbums.FirstOrDefault(a =>
            string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected != null && !ReferenceEquals(SelectedAlbum, selected))
        {
            SelectedAlbum = selected;
        }
        else if (selected == null && string.Equals(SelectedAlbum?.Id, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedAlbum = null;
        }
    }

    private void PreserveSelectedLikedAlbumAfterFilter(string? selectedId)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        var selected = FilteredLikedAlbums.FirstOrDefault(a =>
            string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected != null && !ReferenceEquals(SelectedLikedAlbum, selected))
        {
            SelectedLikedAlbum = selected;
        }
        else if (selected == null && string.Equals(SelectedLikedAlbum?.Id, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLikedAlbum = null;
        }
    }

    /// <summary>
    /// Builds the single-line metadata string for the detail panel. Year + track count
    /// always show; we also append "Added MMM d, yyyy" and — when we have a last-played
    /// timestamp for this album — "Played Xh ago" so the detail panel reflects the user's
    /// relationship to the album at a glance, regardless of the current sort.
    /// </summary>
    private string BuildSelectedAlbumMetadata(LibraryAlbumDto album)
    {
        var parts = new List<string>();
        if (album.Year > 0) parts.Add(album.Year.ToString());
        parts.Add($"{album.TrackCount} tracks");
        if (album.AddedAt > DateTimeOffset.MinValue)
            parts.Add($"Added {album.AddedAt.LocalDateTime:MMM d, yyyy}");
        if (_albumRecents.TryGetValue(album.Id, out var lastPlayed))
            parts.Add(FormatRecentsSubtitle(lastPlayed));
        return string.Join(" • ", parts);
    }

    /// <summary>
    /// Metadata line for a From-Liked-Songs detail-pane hero. Highlights the
    /// liked-tracks-of-total ratio (e.g. "5 of 13 liked"). The year is omitted
    /// because LikedAlbumDto doesn't carry it.
    /// </summary>
    private string BuildSelectedLikedAlbumMetadata(LikedAlbumDto album)
    {
        var parts = new List<string>();
        if (album.TrackCount > 0 && album.TrackCount != album.LikedSongCount)
            parts.Add($"{album.LikedSongCount} of {album.TrackCount} liked");
        else
            parts.Add($"{album.LikedSongCount} liked");

        if (album.MostRecentLikedAt > DateTimeOffset.MinValue)
            parts.Add($"Last liked {album.MostRecentLikedAt.LocalDateTime:MMM d, yyyy}");

        if (_albumRecents.TryGetValue(album.Id, out var lastPlayed))
            parts.Add(FormatRecentsSubtitle(lastPlayed));

        return string.Join(" • ", parts);
    }

    private IEnumerable<LibraryAlbumDto> SortAlbums(IEnumerable<LibraryAlbumDto> source)
    {
        var descending = SortDirection == LibrarySortDirection.Descending;

        return SortBy switch
        {
            // Recents = actual play recency from the Spotify private API (LibraryRecentsService).
            // Never-played items fall to the bottom (desc) or top (asc) via DateTimeOffset.MinValue.
            // Ties are broken by AddedAt descending so the ordering is stable.
            LibrarySortBy.Recents => descending
                ? source.OrderByDescending(a => LastPlayedOrMin(a.Id)).ThenByDescending(a => a.AddedAt)
                : source.OrderBy(a => LastPlayedOrMin(a.Id)).ThenByDescending(a => a.AddedAt),
            // RecentlyAdded keeps its original semantics (library save date).
            LibrarySortBy.RecentlyAdded => descending
                ? source.OrderByDescending(a => a.AddedAt)
                : source.OrderBy(a => a.AddedAt),
            LibrarySortBy.Alphabetical => descending
                ? source.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.Creator => descending
                ? source.OrderByDescending(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.ReleaseDate => descending
                ? source.OrderByDescending(a => a.Year).ThenByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Year).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => source
        };
    }

    /// <summary>
    /// From-Liked-Songs sort. Reuses the same axis labels as the Saved sort
    /// (so the UI panel doesn't need new options) but the semantics shift:
    /// "Recently added" becomes "most recent like across the album's tracks",
    /// and "Release date" falls back to the most-recent-like timestamp because
    /// <see cref="LikedAlbumDto.Year"/> isn't currently populated.
    /// </summary>
    private IEnumerable<LikedAlbumDto> SortLikedAlbums(IEnumerable<LikedAlbumDto> source)
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
            LibrarySortBy.Creator => descending
                ? source.OrderByDescending(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.ReleaseDate => descending
                ? source.OrderByDescending(a => a.MostRecentLikedAt)
                : source.OrderBy(a => a.MostRecentLikedAt),
            _ => source
        };
    }

    private DateTimeOffset LastPlayedOrMin(string albumId) =>
        _albumRecents.TryGetValue(albumId, out var ts) ? ts : DateTimeOffset.MinValue;

    #region ITrackListViewModel Implementation

    // Selection tracking — SelectedItems / SelectedCount / HasSelection /
    // SelectionHeaderText are inherited from TrackListViewModelBase.

    // Sorting track columns - no-op for album tracks (always in track order).
    // Renamed from SortBy to avoid colliding with the LibrarySortBy observable
    // property that drives the library grid's global sort.
    [RelayCommand]
    private void SortTrackColumn(string? columnName) { }

    public string SortChevronGlyph => "";
    public bool IsSortingByTitle => false;
    public bool IsSortingByArtist => false;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    // Multi-select commands
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlaySelectedAsync()
    {
        if (!HasSelection) return;
        var trackUris = SelectedItems
            .Select(item => item is AlbumTrackDto a ? a.Uri : item is LikedSongDto l ? l.Uri : null)
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => u!)
            .ToList();
        if (trackUris.Count == 0) return;
        await _playbackService.PlayTracksAsync(trackUris);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlayAfterAsync()
    {
        if (!HasSelection) return;
        foreach (var item in SelectedItems)
        {
            var uri = item is AlbumTrackDto a ? a.Uri : item is LikedSongDto l ? l.Uri : null;
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
            var uri = item is AlbumTrackDto a ? a.Uri : item is LikedSongDto l ? l.Uri : null;
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
            var uri = item is AlbumTrackDto a ? a.Uri : item is LikedSongDto l ? l.Uri : null;
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

    /// <summary>
    /// Bulk-unlike every Liked-Songs entry that belongs to <paramref name="album"/>.
    /// Caller is responsible for the user confirmation; the VM just drives the
    /// like-service toggles. Incremental refresh via <see cref="OnSaveStateChanged"/>
    /// then drops the album from the grid (because its bucket goes empty).
    /// </summary>
    public void UnlikeAllSongsFromLikedAlbum(LikedAlbumDto album)
    {
        if (album == null || LikeService == null) return;
        foreach (var track in album.LikedSongs)
        {
            if (string.IsNullOrEmpty(track.Uri)) continue;
            LikeService.ToggleSave(SavedItemType.Track, track.Uri, currentlySaved: true);
        }
    }

    /// <summary>
    /// Removes a directly hearted album from the user's album library without
    /// touching any liked songs that happen to belong to it.
    /// </summary>
    public void UnheartAlbum(LibraryAlbumDto album)
    {
        if (album == null || LikeService == null || string.IsNullOrEmpty(album.Id)) return;
        LikeService.ToggleSave(SavedItemType.Album, album.Id, currentlySaved: true);
    }

    protected override void OnSelectionChanged()
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlayAfterCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachLongLivedServices();
    }
}
