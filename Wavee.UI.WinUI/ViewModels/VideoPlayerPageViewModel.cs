using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Library.Local;
using Wavee.Protocol.Metadata;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Windows.Media.Playback;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Backs the YouTube-style <c>VideoPlayerPage</c>. Source-agnostic — consumes
/// <see cref="IPlaybackStateService"/> for metadata and
/// <see cref="IActiveVideoSurfaceService"/> for the active provider's
/// <see cref="MediaPlayer"/> + <c>Kind</c>. The page binds its
/// <c>MediaPlayerElement</c> via the surface service (not directly to a
/// concrete engine), so a future Spotify video engine works the same way.
/// </summary>
public sealed partial class VideoPlayerPageViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService? _state;
    private readonly IActiveVideoSurfaceService _surface;
    private readonly ILocalLibraryService? _localLibrary;
    private readonly IPathfinderClient? _pathfinder;
    private readonly IExtendedMetadataClient? _metadata;
    private readonly ITrackLikeService? _likeService;
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly ISpotifyVideoPlaybackDetails? _videoPlaybackDetails;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private int _spotifyDetailsVersion;
    private int _saveStateVersion;
    private bool _disposed;

    // Strings here drive source-conditional UI:
    //   "local"               → show parent-folder chip, "Open file location"
    //   "spotify-music-video" → show artist chip, "Open in Spotify"
    //   "spotify-podcast-video" → show show-name chip, "Open in Spotify"
    [ObservableProperty] private string? _activeKind;

    // Bound by the page; reflects IPlaybackStateService for free.
    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _subtitleLine;       // "filename · 1:23:45 · 1.2 GB" / "Artist · 2024 · 4:15"
    [ObservableProperty] private string? _pathOrSecondary;    // absolute path for local; album for Spotify
    [ObservableProperty] private string? _sourceChipText;     // folder name / artist / show name
    [ObservableProperty] private string? _sourceChipImageUrl; // null for local; avatar/album art for Spotify
    [ObservableProperty] private bool _isSpotifyVideoDetailsVisible;
    [ObservableProperty] private string? _spotifyVideoTitle;
    [ObservableProperty] private string? _spotifyVideoImageUrl;
    [ObservableProperty] private string? _spotifyVideoAlbumLine;
    [ObservableProperty] private string? _spotifyVideoReleaseLine;
    [ObservableProperty] private string? _spotifyVideoCreditsLine;
    [ObservableProperty] private string? _spotifyArtistHeaderImageUrl;
    [ObservableProperty] private string? _spotifyArtistAvatarUrl;
    [ObservableProperty] private string? _spotifyArtistStatsLine;
    [ObservableProperty] private string? _spotifyArtistTopCitiesLine;
    [ObservableProperty] private string? _spotifyArtistBioLine;
    [ObservableProperty] private string? _spotifyRelatedVideosLine;
    [ObservableProperty] private bool _isSpotifyPlaybackDetailsVisible;
    [ObservableProperty] private string? _spotifyVideoQualityLine;
    [ObservableProperty] private string? _spotifyVideoDrmLine;
    [ObservableProperty] private string? _spotifyVideoDashLine;
    [ObservableProperty] private Brush? _paletteBackdropBrush;
    [ObservableProperty] private Brush? _paletteHeroGradientBrush;
    [ObservableProperty] private Brush? _paletteAccentPillBrush;
    [ObservableProperty] private Brush? _paletteAccentPillForegroundBrush;
    [ObservableProperty] private bool _isSaved;

    public ObservableCollection<UpNextItem> UpNext { get; } = new();

    /// <summary>
    /// Cleaner binding target for the empty-state vs list switch on the
    /// "Up next" panel — ObservableCollection.Count doesn't push notifications
    /// through XAML bindings on its own. Updated in OnUpNextChanged below.
    /// </summary>
    [ObservableProperty] private bool _hasUpNext;

    // Source-conditional visibilities flipped by ActiveKind.
    public bool ShowOpenFolder => ActiveKind == "local";
    public bool ShowOpenInSpotify => ActiveKind?.StartsWith("spotify-", StringComparison.Ordinal) == true;

    public IRelayCommand<string?> PlayUpNextCommand { get; }
    public IRelayCommand OpenSourceCommand { get; }
    public IRelayCommand ToggleLikeCommand { get; }

    /// <summary>
    /// Shared singleton <see cref="PlayerBarViewModel"/>. The video page's
    /// transport overlay (play/pause/prev/next/seek/volume) reuses these
    /// commands so the page surface and the bottom <c>PlayerBar</c> are the
    /// same control plane — no second state machine, no risk of divergence.
    /// Same pattern as <c>MiniVideoPlayerViewModel.Player</c>.
    /// </summary>
    public PlayerBarViewModel? Player { get; }

    public VideoPlayerPageViewModel(
        IActiveVideoSurfaceService surface,
        IPlaybackStateService? state,
        ILocalLibraryService? localLibrary,
        IPathfinderClient? pathfinder = null,
        IExtendedMetadataClient? metadata = null,
        ILogger<VideoPlayerPageViewModel>? logger = null)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _state = state;
        _localLibrary = localLibrary;
        _pathfinder = pathfinder;
        _metadata = metadata;
        _likeService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ITrackLikeService>();
        _musicVideoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<IMusicVideoMetadataService>();
        _videoPlaybackDetails = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ISpotifyVideoPlaybackDetails>();
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException(
                          "VideoPlayerPageViewModel must be constructed on the UI thread.");

        PlayUpNextCommand = new RelayCommand<string?>(uri => _ = PlayUriAsync(uri));
        OpenSourceCommand = new RelayCommand(OpenSource);
        ToggleLikeCommand = new RelayCommand(ToggleLike);

        // Resolve the singleton PlayerBarViewModel once at ctor — guaranteed
        // present (registered as singleton in AppLifecycleHelper). Used by the
        // transport overlay on the video frame.
        Player = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<PlayerBarViewModel>();

        ActiveKind = _surface.ActiveKind;
        _surface.ActiveSurfaceChanged += OnActiveSurfaceChanged;
        UpNext.CollectionChanged += (_, _) => HasUpNext = UpNext.Count > 0;

        if (_state is not null)
            _state.PropertyChanged += OnPlaybackStateChanged;
        if (_likeService is not null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        if (_videoPlaybackDetails is not null)
            _videoPlaybackDetails.PropertyChanged += OnVideoPlaybackDetailsChanged;

        // Initial population.
        Refresh();
        RefreshVideoPlaybackDetails();
    }

    private void OnActiveSurfaceChanged(object? sender, MediaPlayer? surface)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnActiveSurfaceChanged(sender, surface));
            return;
        }
        ActiveKind = _surface.ActiveKind;
        OnPropertyChanged(nameof(ShowOpenFolder));
        OnPropertyChanged(nameof(ShowOpenInSpotify));
        Refresh();
    }

    private void OnPlaybackStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        // Anything that affects derived display lines.
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackTitle)
            or nameof(IPlaybackStateService.CurrentArtistName)
            or nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.Duration)
            or nameof(IPlaybackStateService.CurrentAlbumArtColor)
            or nameof(IPlaybackStateService.CurrentTrackIsVideo)
            or nameof(IPlaybackStateService.CurrentOriginalTrackId)
            or nameof(IPlaybackStateService.CurrentOriginalTrackTitle)
            or nameof(IPlaybackStateService.CurrentOriginalArtistName)
            or nameof(IPlaybackStateService.CurrentOriginalAlbumArt)
            or nameof(IPlaybackStateService.CurrentOriginalAlbumArtLarge)
            or nameof(IPlaybackStateService.CurrentOriginalDuration))
        {
            if (!_dispatcher.HasThreadAccess) _dispatcher.TryEnqueue(Refresh);
            else Refresh();
        }
    }

    private void OnSaveStateChanged()
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess) _dispatcher.TryEnqueue(UpdateSavedState);
        else UpdateSavedState();
    }

    private void OnVideoPlaybackDetailsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess) _dispatcher.TryEnqueue(RefreshVideoPlaybackDetails);
        else RefreshVideoPlaybackDetails();
    }

    private void Refresh()
    {
        Title = _state?.CurrentTrackTitle ?? "Video";

        // Strategy: source-conditional secondary lines.
        switch (ActiveKind)
        {
            case "local":
                BuildLocalDisplay();
                _ = LoadLocalUpNextAsync();
                break;
            case "spotify-music-video":
            case "spotify-podcast-video":
                BuildSpotifyDisplay();
                LoadSpotifyUpNext();
                break;
            default:
                // No active provider — leave subtitle/chip blank.
                SubtitleLine = null;
                PathOrSecondary = null;
                SourceChipText = null;
                SourceChipImageUrl = null;
                ClearSpotifyVideoDetails();
                UpNext.Clear();
                break;
        }

        UpdateSavedState();
    }

    private void BuildLocalDisplay()
    {
        ClearSpotifyVideoDetails();

        // Always set a default so the chip never renders blank, even when
        // the async row lookup hasn't completed (or _state.CurrentTrackId
        // hasn't propagated yet at first Refresh()).
        SourceChipText = "Local files";
        SourceChipImageUrl = null;

        // For local, _state's CurrentTrackId is the wavee:local:track:* URI.
        // We get the row from the DB to pull file path + duration + real
        // folder name (overrides the "Local files" default once it lands).
        var trackUri = _state?.CurrentTrackId;
        if (string.IsNullOrEmpty(trackUri) || _localLibrary is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var row = await _localLibrary.GetTrackAsync(trackUri).ConfigureAwait(false);
                if (row is null || _disposed) return;
                _dispatcher.TryEnqueue(() =>
                {
                    var filename = Path.GetFileName(row.FilePath);
                    var folder = Path.GetFileName(Path.GetDirectoryName(row.FilePath) ?? "");
                    if (string.IsNullOrEmpty(folder)) folder = "Local files";
                    var dur = FormatDuration(row.DurationMs);
                    var size = FormatFileSize(row.FilePath);
                    SubtitleLine = string.Join(" · ", new[] { filename, dur, size }.Where(s => !string.IsNullOrEmpty(s)));
                    PathOrSecondary = row.FilePath;
                    SourceChipText = folder;
                });
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "BuildLocalDisplay failed for {Uri}", trackUri);
            }
        });
    }

    private void BuildSpotifyDisplay()
    {
        // Spotify metadata is already in IPlaybackStateService — the page
        // doesn't need to look anywhere else.
        Title = _state?.CurrentOriginalTrackTitle
                ?? _state?.CurrentTrackTitle
                ?? "Video";
        var artist = _state?.CurrentOriginalArtistName ?? _state?.CurrentArtistName ?? "";
        var originalDuration = _state?.CurrentOriginalDuration ?? 0;
        var duration = originalDuration > 0
            ? originalDuration
            : (_state?.Duration ?? 0);
        var dur = FormatDuration((long)duration);
        SubtitleLine = string.Join(" · ", new[] { artist, dur }.Where(s => !string.IsNullOrEmpty(s)));
        PathOrSecondary = null; // Could pull album/show name later.
        SourceChipText = artist;
        SourceChipImageUrl = _state?.CurrentOriginalAlbumArtLarge
                             ?? _state?.CurrentOriginalAlbumArt
                             ?? _state?.CurrentAlbumArtLarge
                             ?? _state?.CurrentAlbumArt;
        ApplyPalette(isDark: true);
        _ = LoadSpotifyVideoDetailsAsync();
    }

    private async Task LoadSpotifyVideoDetailsAsync()
    {
        var version = ++_spotifyDetailsVersion;
        var audioUri = GetCurrentOriginalSpotifyTrackUri() ?? GetCurrentSpotifyTrackUri();
        var artistUri = _state?.CurrentOriginalArtistId ?? _state?.CurrentArtistId;
        if (string.IsNullOrEmpty(audioUri) || string.IsNullOrEmpty(artistUri))
        {
            ClearSpotifyVideoDetails();
            return;
        }

        try
        {
            var npvTask = _pathfinder?.GetNpvArtistAsync(artistUri, audioUri) ?? Task.FromResult<NpvArtistResponse?>(null);
            var trackTask = _metadata?.GetTrackAsync(audioUri) ?? Task.FromResult<Track?>(null);

            await Task.WhenAll(npvTask, trackTask).ConfigureAwait(false);
            var npv = await npvTask.ConfigureAwait(false);
            var videoTrack = await trackTask.ConfigureAwait(false);

            if (_disposed || version != _spotifyDetailsVersion) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || version != _spotifyDetailsVersion) return;
                ApplySpotifyVideoDetails(npv, videoTrack, _state?.CurrentTrackIsVideo == true);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Spotify video details load failed for {Track}", audioUri);
        }
    }

    private void ApplySpotifyVideoDetails(NpvArtistResponse? npv, Track? videoTrack, bool isLinkedVideo)
    {
        var artist = npv?.Data?.ArtistUnion;
        var trackUnion = npv?.Data?.TrackUnion;

        SpotifyVideoTitle = videoTrack?.Name;
        SpotifyVideoImageUrl = GetAlbumImageUrl(videoTrack?.Album, Image.Types.Size.Large)
            ?? _state?.CurrentAlbumArtLarge
            ?? _state?.CurrentAlbumArt;

        var videoLabel = isLinkedVideo ? "Video track" : "Track";
        var artistNames = videoTrack?.Artist.Count > 0
            ? string.Join(", ", videoTrack.Artist.Select(a => a.Name).Where(s => !string.IsNullOrWhiteSpace(s)))
            : _state?.CurrentArtistName;
        SpotifyVideoAlbumLine = string.Join(" · ", new[]
        {
            videoLabel,
            videoTrack?.Album?.Name,
            artistNames,
            FormatDuration(videoTrack?.Duration ?? 0)
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        SpotifyVideoReleaseLine = string.Join(" · ", new[]
        {
            FormatDate(videoTrack?.Album?.Date),
            videoTrack?.Album?.Label,
            GetExternalId(videoTrack, "isrc"),
            videoTrack?.Popularity > 0 ? $"Popularity {videoTrack.Popularity}" : null,
            videoTrack?.EarliestLiveTimestamp > 0 ? $"Live {FormatUnixDate(videoTrack.EarliestLiveTimestamp)}" : null
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        SpotifyVideoCreditsLine = FormatCredits(trackUnion);
        SpotifyArtistHeaderImageUrl = BestImageUrl(artist?.HeaderImage?.Data?.Sources);
        SpotifyArtistAvatarUrl = BestImageUrl(artist?.Visuals?.AvatarImage?.Sources) ?? SourceChipImageUrl;
        SpotifyArtistStatsLine = FormatArtistStats(artist?.Stats);
        SpotifyArtistTopCitiesLine = FormatTopCities(artist?.Stats);
        SpotifyArtistBioLine = CleanBiography(artist?.Profile?.Biography?.Text);
        SpotifyRelatedVideosLine = trackUnion?.RelatedVideos?.TotalCount > 0
            ? $"{trackUnion.RelatedVideos.TotalCount:N0} related videos"
            : null;

        IsSpotifyVideoDetailsVisible = true;
    }

    private void ClearSpotifyVideoDetails()
    {
        _spotifyDetailsVersion++;
        IsSpotifyVideoDetailsVisible = false;
        SpotifyVideoTitle = null;
        SpotifyVideoImageUrl = null;
        SpotifyVideoAlbumLine = null;
        SpotifyVideoReleaseLine = null;
        SpotifyVideoCreditsLine = null;
        SpotifyArtistHeaderImageUrl = null;
        SpotifyArtistAvatarUrl = null;
        SpotifyArtistStatsLine = null;
        SpotifyArtistTopCitiesLine = null;
        SpotifyArtistBioLine = null;
        SpotifyRelatedVideosLine = null;
    }

    private void RefreshVideoPlaybackDetails()
    {
        var metadata = _videoPlaybackDetails?.PlaybackMetadata;
        var quality = _videoPlaybackDetails?.CurrentQuality;
        if (metadata is null && quality is null)
        {
            IsSpotifyPlaybackDetailsVisible = false;
            SpotifyVideoQualityLine = null;
            SpotifyVideoDrmLine = null;
            SpotifyVideoDashLine = null;
            return;
        }

        SpotifyVideoQualityLine = quality is null
            ? null
            : $"Current quality: {quality.Label} ({quality.Codec})";

        SpotifyVideoDrmLine = metadata is null
            ? null
            : string.Join(" - ", new[]
            {
                metadata.DrmSystem,
                metadata.Container,
                string.IsNullOrWhiteSpace(metadata.LicenseServerEndpoint)
                    ? "license endpoint unavailable"
                    : "license endpoint provided"
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        SpotifyVideoDashLine = metadata is null
            ? null
            : $"{metadata.SegmentCount:N0} segments - {metadata.SegmentLengthSeconds}s chunks - {FormatDuration(metadata.DurationMs)}";

        IsSpotifyPlaybackDetailsVisible = true;
    }

    public void ApplyTheme(bool isDark) => ApplyPalette(isDark);

    private void ApplyPalette(bool isDark)
    {
        if (!TryParseHexColor(_state?.CurrentAlbumArtColor, out var baseColor))
        {
            PaletteBackdropBrush = null;
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            return;
        }

        var bg = Darken(baseColor, isDark ? 0.38 : 0.18);
        var bgTint = Lighten(baseColor, isDark ? 0.08 : 0.24);
        var accent = Lighten(baseColor, isDark ? 0.20 : 0.02);

        PaletteBackdropBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 58 : 36), bg.R, bg.G, bg.B));

        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(236, bgTint.R, bgTint.G, bgTint.B), Offset = 0.0 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(174, bg.R, bg.G, bg.B), Offset = 0.38 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(78, bg.R, bg.G, bg.B), Offset = 0.68 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, bg.R, bg.G, bg.B), Offset = 1.0 });
        PaletteHeroGradientBrush = heroGrad;

        PaletteAccentPillBrush = new SolidColorBrush(accent);
        var accentLuma = (accent.R * 299 + accent.G * 587 + accent.B * 114) / 1000;
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
    }

    private Task LoadLocalUpNextAsync()
    {
        // Stub — the previous implementation surfaced "all other local tracks"
        // which is a library browse list, not a real playback queue. Skipping
        // Next from there ended playback (engine queue was unrelated to the
        // panel), which was confusing. Up next will be re-implemented on top
        // of IPlaybackStateService's queue snapshot in a follow-up; until
        // then the panel renders the existing "Nothing queued" empty state.
        if (_disposed) return Task.CompletedTask;
        if (_dispatcher.HasThreadAccess) UpNext.Clear();
        else _dispatcher.TryEnqueue(UpNext.Clear);
        return Task.CompletedTask;
    }

    private void LoadSpotifyUpNext()
    {
        // Stub — same reason as LoadLocalUpNextAsync. Will populate from the
        // real engine queue (IPlaybackStateService.NextTracks or equivalent)
        // in a follow-up.
        UpNext.Clear();
    }

    private async Task PlayUriAsync(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var engine = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Connect.IPlaybackEngine>();
        if (engine is null) return;
        try
        {
            await engine.PlayAsync(new Wavee.Connect.Commands.PlayCommand
            {
                Endpoint = "play",
                Key = "video-up-next/0",
                MessageId = 0,
                MessageIdent = "video-up-next",
                SenderDeviceId = "",
                ContextUri = uri,
                TrackUri = uri,
                ContextDescription = "Up next",
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Up next play failed for {Uri}", uri);
        }
    }

    private void OpenSource()
    {
        if (ActiveKind == "local" && !string.IsNullOrEmpty(PathOrSecondary))
        {
            try { Process.Start("explorer", $"/select,\"{PathOrSecondary}\""); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Open file location failed"); }
            return;
        }
        // Spotify: deeplink via spotify: URI scheme — handled later.
    }

    private void ToggleLike()
        => _ = ToggleLikeAsync();

    private async Task ToggleLikeAsync()
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_state, _musicVideoMetadata)
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(uri) || _likeService is null) return;

        var wasSaved = _likeService.IsSaved(SavedItemType.Track, uri);
        _likeService.ToggleSave(SavedItemType.Track, uri, wasSaved);
        IsSaved = !wasSaved;
    }

    private void UpdateSavedState()
    {
        var version = ++_saveStateVersion;
        var uri = PlaybackSaveTargetResolver.GetTrackUri(_state);
        if (!string.IsNullOrEmpty(uri))
        {
            IsSaved = _likeService?.IsSaved(SavedItemType.Track, uri) == true;
            return;
        }

        IsSaved = false;
        _ = UpdateSavedStateAsync(version);
    }

    private async Task UpdateSavedStateAsync(int version)
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_state, _musicVideoMetadata)
            .ConfigureAwait(true);

        if (version != _saveStateVersion)
            return;

        IsSaved = !string.IsNullOrEmpty(uri)
            && _likeService?.IsSaved(SavedItemType.Track, uri) == true;
    }

    private static string FormatDuration(long ms)
    {
        if (ms <= 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private static string FormatFileSize(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            if (size >= 1L << 30) return $"{size / (double)(1L << 30):0.##} GB";
            if (size >= 1L << 20) return $"{size / (double)(1L << 20):0.#} MB";
            if (size >= 1L << 10) return $"{size / (double)(1L << 10):0.#} KB";
            return $"{size} B";
        }
        catch { return ""; }
    }

    private string? GetCurrentSpotifyTrackUri()
    {
        var trackId = _state?.CurrentTrackId;
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        return trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? trackId
            : $"spotify:track:{trackId}";
    }

    private string? GetCurrentOriginalSpotifyTrackUri()
    {
        var trackId = _state?.CurrentOriginalTrackId;
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        return trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? trackId
            : $"spotify:track:{trackId}";
    }

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                color = Color.FromArgb(
                    255,
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
                return true;
            }

            if (hex.Length == 8)
            {
                color = Color.FromArgb(
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16),
                    Convert.ToByte(hex[6..8], 16));
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static Color Lighten(Color color, double amount)
        => Mix(color, Color.FromArgb(255, 255, 255, 255), amount);

    private static Color Darken(Color color, double amount)
        => Mix(color, Color.FromArgb(255, 0, 0, 0), amount);

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
    }

    private static string? GetAlbumImageUrl(Album? album, Image.Types.Size preferredSize = Image.Types.Size.Default)
    {
        var image = album?.CoverGroup?.Image.FirstOrDefault(i => i.Size == preferredSize)
                    ?? album?.CoverGroup?.Image.FirstOrDefault();
        if (image?.FileId is null || image.FileId.Length == 0) return null;

        var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
        return $"spotify:image:{imageId}";
    }

    private static string? FormatDate(Date? date)
    {
        if (date?.Year is not > 0) return null;

        if (date.Month is > 0 && date.Day is > 0)
        {
            try
            {
                return new DateTime(date.Year, date.Month, date.Day)
                    .ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
            }
            catch
            {
                return date.Year.ToString(CultureInfo.CurrentCulture);
            }
        }

        if (date.Month is > 0)
        {
            try
            {
                return new DateTime(date.Year, date.Month, 1)
                    .ToString("MMM yyyy", CultureInfo.CurrentCulture);
            }
            catch
            {
                return date.Year.ToString(CultureInfo.CurrentCulture);
            }
        }

        return date.Year.ToString(CultureInfo.CurrentCulture);
    }

    private static string? FormatUnixDate(long seconds)
    {
        if (seconds <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds)
            .ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static string? GetExternalId(Track? track, string type)
    {
        var id = track?.ExternalId.FirstOrDefault(e =>
            string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : $"{type.ToUpperInvariant()} {id}";
    }

    private static string? FormatCredits(NpvTrackUnion? trackUnion)
    {
        var contributors = trackUnion?.CreditsTrait?.Contributors?.Items;
        if (contributors is { Count: > 0 })
        {
            var selected = contributors
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Role))
                .Select(c => $"{c.Role} {c.Name}")
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();

            if (selected.Length > 0)
                return string.Join(" · ", selected);
        }

        var credits = trackUnion?.Credits;
        if (credits is not { Count: > 0 }) return null;

        var fallback = credits
            .Where(c => !string.IsNullOrWhiteSpace(c.ArtistName) && !string.IsNullOrWhiteSpace(c.Role))
            .Select(c => $"{c.Role} {c.ArtistName}")
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        return fallback.Length == 0 ? null : string.Join(" · ", fallback);
    }

    private static string? BestImageUrl(IReadOnlyList<ArtistImageSource>? sources)
    {
        return sources?
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .OrderByDescending(s => s.Width ?? s.MaxWidth ?? 0)
            .ThenByDescending(s => s.Height ?? s.MaxHeight ?? 0)
            .FirstOrDefault()?.Url;
    }

    private static string? FormatArtistStats(ArtistStats? stats)
    {
        if (stats is null) return null;

        var parts = new[]
        {
            stats.MonthlyListeners > 0 ? $"{stats.MonthlyListeners:N0} monthly listeners" : null,
            stats.Followers > 0 ? $"{stats.Followers:N0} followers" : null
        }.Where(s => !string.IsNullOrWhiteSpace(s));

        var line = string.Join(" · ", parts);
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static string? FormatTopCities(ArtistStats? stats)
    {
        var cities = stats?.TopCities?.Items?
            .Where(c => !string.IsNullOrWhiteSpace(c.City))
            .Take(3)
            .Select(c => c.NumberOfListeners > 0
                ? $"{c.City} ({c.NumberOfListeners:N0})"
                : c.City!)
            .ToArray();

        return cities is { Length: > 0 } ? $"Top cities: {string.Join(", ", cities)}" : null;
    }

    private static string? CleanBiography(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var decoded = WebUtility.HtmlDecode(text).Trim();
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.ActiveSurfaceChanged -= OnActiveSurfaceChanged;
        if (_state is not null) _state.PropertyChanged -= OnPlaybackStateChanged;
        if (_likeService is not null) _likeService.SaveStateChanged -= OnSaveStateChanged;
        if (_videoPlaybackDetails is not null) _videoPlaybackDetails.PropertyChanged -= OnVideoPlaybackDetailsChanged;
    }
}

/// <summary>Row in the "Up next" panel — same shape regardless of source.</summary>
/// <param name="Uri">Track URI (local or Spotify).</param>
/// <param name="Title">Display title.</param>
/// <param name="Subtitle">Secondary line (artist / album / publisher).</param>
/// <param name="ImageUrl">Thumbnail URL or null.</param>
/// <param name="DurationText">"1:23:45" / "4:15" / "" if unknown.</param>
/// <param name="IsVideo">Drives the small video glyph in the corner.</param>
public sealed record UpNextItem(
    string Uri,
    string Title,
    string Subtitle,
    string? ImageUrl,
    string DurationText,
    bool IsVideo);
