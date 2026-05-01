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
using Wavee.Core.Storage;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.Protocol.Metadata;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
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
    private readonly ILyricsService? _lyricsService;
    private readonly ISpotifyVideoPlaybackDetails? _videoPlaybackDetails;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private int _spotifyDetailsVersion;
    private int _saveStateVersion;
    private bool _disposed;

    // Cached theme so palette refreshes triggered by track changes (Refresh →
    // BuildSpotifyDisplay → ApplyPalette) honour the actual page theme. Mirrors
    // the pattern used in AlbumViewModel/ConcertViewModel/PlaylistViewModel.
    // Pre-fix this VM hard-coded ApplyPalette(true) on every track change which
    // silently overwrote the light-theme palette tier on every Spotify track.
    private bool _isDarkTheme = true;

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

    // Drives the labelled "Save"/"Saved" pill text in the redesigned hero
    // action bar. Updated alongside IsSaved by UpdateSavedState; partial method
    // keeps the two in sync if external code sets IsSaved directly.
    [ObservableProperty] private string _saveLabelText = "Save";

    // Drives the secondary "Open source" pill: folder glyph + "Show in folder"
    // tooltip for local sources, world/share glyph + "Open in Spotify" for
    // Spotify sources. Replaces the old pair of conditionally-visible buttons
    // (lines 438-454 of pre-redesign XAML) with a single icon button whose
    // glyph and tooltip swap on ActiveKind.
    //   ED25 = Folder
    //   E8A7 = OpenInNewWindow (used by the desktop client for "open in app")
    [ObservableProperty] private string _sourceActionGlyph = FluentGlyphs.OpenInNewWindow;
    [ObservableProperty] private string _sourceActionTooltip = "Open source";

    // Description-card navigation surface. The track and artist sub-cards are
    // clickable Buttons whose Command targets are gated on these URIs being
    // non-null — so for local sources / failed metadata fetches the cards
    // simply don't render. URIs are derived from the Spotify track/artist
    // metadata in ApplySpotifyVideoDetails using the same SpotifyId.FromRaw +
    // ToBase62 pattern the metadata enricher uses (TrackMetadataEnricher.cs:108).
    [ObservableProperty] private string? _spotifyArtistUri;
    [ObservableProperty] private string? _spotifyAlbumUri;

    // Drives the bio "Read more" / "Read less" toggle. BioMaxLines collapses
    // to 3 when false, expands (MaxLines=0 → unlimited in WinUI) when true.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BioMaxLines))]
    [NotifyPropertyChangedFor(nameof(BioReadMoreLabel))]
    private bool _spotifyArtistBioExpanded;

    // Drives the Stream-details Expander; default closed so the playback
    // diagnostics (quality / DRM / DASH segment counts) don't clutter the
    // card for normal users.
    [ObservableProperty] private bool _isStreamDetailsExpanded;

    // Track sub-card title — flips between "About this track" and "From the
    // audio track" depending on whether the current item is a music video
    // with a linked audio twin (set in ApplySpotifyVideoDetails).
    [ObservableProperty] private string _trackCardHeaderText = "About this track";

    /// <summary>
    /// MaxLines binding for the artist bio. WinUI treats <c>MaxLines=0</c> as
    /// "unlimited" — so this returns 0 when the user has tapped Read more.
    /// </summary>
    public int BioMaxLines => SpotifyArtistBioExpanded ? 0 : 3;

    /// <summary>Hyperlink label that flips with bio expansion state.</summary>
    public string BioReadMoreLabel => SpotifyArtistBioExpanded ? "Read less" : "Read more";

    // Cached album name for the OpenAlbumCommand "title" arg passed to
    // NavigationHelpers.OpenAlbum (which uses it for the tab label). Not an
    // observable — it's pure command argument state.
    private string? _spotifyAlbumName;

    // Playcount for the currently-playing track. Pulled from the getTrack
    // Pathfinder query (Track protobuf and npvArtist don't carry it).
    // Null when no playcount available — drives the line's visibility via
    // NullToCollapsedConverter on the identity row.
    [ObservableProperty] private string? _playCountText;

    // Audio vs video presentation. False = video frame (default), true =
    // album-art card with lyrics toggle. Driven by CurrentTrackIsVideo on
    // the playback state service — set in Refresh() per track change.
    [ObservableProperty] private bool _isAudioMode;

    // Inside audio mode, swap the album art for the synced lyrics canvas.
    // Default false (album art shows). Toggled by a button in the
    // transport overlay's right cluster.
    [ObservableProperty] private bool _isLyricsVisible;

    // Lyrics for the currently-playing track. Set on track change via
    // LoadLyricsAsync (uses ILyricsService — same provider chain the
    // popout's ExpandedPlayerView uses). Code-behind subscribes to
    // PropertyChanged and pushes into NowPlayingCanvas.SetLyricsData.
    // Null when lyrics aren't available; the canvas shows its own
    // empty state in that case.
    [ObservableProperty] private LyricsData? _lyricsData;

    // Token version for the in-flight lyrics fetch. Increments on every
    // track change so a slow fetch from the previous track doesn't apply
    // its result to the new track. Same pattern as _spotifyDetailsVersion.
    private int _lyricsVersion;

    /// <summary>
    /// YouTube-style "Recommended" list (chip-tab default). Vertical cards
    /// in the right panel. Backed by Spotify's
    /// <c>internalLinkRecommenderTrack</c> Pathfinder query — same data
    /// that powers the desktop player's Recommended sidebar.
    /// </summary>
    public ObservableCollection<WatchNextItem> WatchNext { get; } = new();

    /// <summary>True when <see cref="WatchNext"/> has at least one item.</summary>
    [ObservableProperty] private bool _hasWatchNext;

    /// <summary>
    /// "Music videos" list (chip-tab #2). Other music videos by the current
    /// track's artist, populated from <c>npv.TrackUnion.RelatedVideos</c>.
    /// </summary>
    public ObservableCollection<RelatedVideoItem> RelatedVideos { get; } = new();

    /// <summary>True when <see cref="RelatedVideos"/> has at least one item.</summary>
    [ObservableProperty] private bool _hasRelatedVideos;

    // Per-tab loading/error/empty status. Drives the right-panel section to
    // pick exactly one of Loading / Loaded / Empty / Error rather than
    // collapsing to nothing when an API call fails or returns 0 items.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecommendedLoading))]
    [NotifyPropertyChangedFor(nameof(IsRecommendedLoaded))]
    [NotifyPropertyChangedFor(nameof(IsRecommendedEmpty))]
    [NotifyPropertyChangedFor(nameof(IsRecommendedError))]
    private DataLoadStatus _recommendedStatus = DataLoadStatus.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideosLoading))]
    [NotifyPropertyChangedFor(nameof(IsVideosLoaded))]
    [NotifyPropertyChangedFor(nameof(IsVideosEmpty))]
    [NotifyPropertyChangedFor(nameof(IsVideosError))]
    private DataLoadStatus _videosStatus = DataLoadStatus.Loading;

    public bool IsRecommendedLoading => RecommendedStatus == DataLoadStatus.Loading;
    public bool IsRecommendedLoaded => RecommendedStatus == DataLoadStatus.Loaded;
    public bool IsRecommendedEmpty => RecommendedStatus == DataLoadStatus.Empty;
    public bool IsRecommendedError => RecommendedStatus == DataLoadStatus.Error;

    public bool IsVideosLoading => VideosStatus == DataLoadStatus.Loading;
    public bool IsVideosLoaded => VideosStatus == DataLoadStatus.Loaded;
    public bool IsVideosEmpty => VideosStatus == DataLoadStatus.Empty;
    public bool IsVideosError => VideosStatus == DataLoadStatus.Error;

    // Right-panel chip-tab selection. Controls which section the right
    // panel renders: the SEO recommender list (default), the npv
    // music-videos list, or the real playback queue (QueueControl). The
    // chips are rendered by SessionTokenView (vendored CommunityToolkit
    // Labs TokenView, already used on PlaylistPage), bound to the
    // RightPanelTabs collection with two-way SelectedRightPanelTab.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecommendedTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsVideosTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsQueueTabSelected))]
    private RightPanelTabChip? _selectedRightPanelTab;

    public IReadOnlyList<RightPanelTabChip> RightPanelTabs { get; }

    public bool IsRecommendedTabSelected => SelectedRightPanelTab?.Tab == RightPanelTab.Recommended;
    public bool IsVideosTabSelected => SelectedRightPanelTab?.Tab == RightPanelTab.Videos;
    public bool IsQueueTabSelected => SelectedRightPanelTab?.Tab == RightPanelTab.Queue;

    // Source-conditional visibilities flipped by ActiveKind.
    public bool ShowOpenFolder => ActiveKind == "local";
    public bool ShowOpenInSpotify => ActiveKind?.StartsWith("spotify-", StringComparison.Ordinal) == true;

    public IRelayCommand OpenSourceCommand { get; }
    public IRelayCommand ToggleLikeCommand { get; }

    // Description-card navigation. Both no-op silently when their URI is
    // null (local source, or metadata fetch failed) — the bound buttons
    // are also Visibility-gated on the URI in XAML so the user never sees
    // a dead button.
    public IRelayCommand OpenArtistCommand { get; }
    public IRelayCommand OpenAlbumCommand { get; }

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
        _lyricsService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ILyricsService>();
        _videoPlaybackDetails = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ISpotifyVideoPlaybackDetails>();
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException(
                          "VideoPlayerPageViewModel must be constructed on the UI thread.");

        OpenSourceCommand = new RelayCommand(OpenSource);
        ToggleLikeCommand = new RelayCommand(ToggleLike);
        OpenArtistCommand = new RelayCommand(OpenArtist);
        OpenAlbumCommand = new RelayCommand(OpenAlbum);

        // Build the chip-tab list once. The SessionTokenView rendering them
        // in XAML expects a stable IReadOnlyList — never repopulated.
        // SelectedRightPanelTab defaults to the first chip (Recommended).
        var tabs = new[]
        {
            new RightPanelTabChip("Recommended", RightPanelTab.Recommended),
            new RightPanelTabChip("Music videos", RightPanelTab.Videos),
            new RightPanelTabChip("Queue", RightPanelTab.Queue),
        };
        RightPanelTabs = tabs;
        SelectedRightPanelTab = tabs[0];

        // Resolve the singleton PlayerBarViewModel once at ctor — guaranteed
        // present (registered as singleton in AppLifecycleHelper). Used by the
        // transport overlay on the video frame.
        Player = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<PlayerBarViewModel>();

        ActiveKind = _surface.ActiveKind;
        _surface.ActiveSurfaceChanged += OnActiveSurfaceChanged;

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
        var effectiveKind = GetEffectiveActiveKind();
        if (!string.Equals(ActiveKind, effectiveKind, StringComparison.Ordinal))
        {
            ActiveKind = effectiveKind;
            OnPropertyChanged(nameof(ShowOpenFolder));
            OnPropertyChanged(nameof(ShowOpenInSpotify));
        }

        // Strategy: source-conditional secondary lines. The right-panel
        // queue is owned by QueueControl (subscribes to IPlaybackStateService
        // directly), so this VM no longer maintains its own UpNext list.
        switch (effectiveKind)
        {
            case "local":
                BuildLocalDisplay();
                SourceActionGlyph = FluentGlyphs.FolderOpen;
                SourceActionTooltip = "Show in folder";
                break;
            case "spotify-music-video":
            case "spotify-podcast-video":
            case "spotify-audio":
                BuildSpotifyDisplay();
                SourceActionGlyph = FluentGlyphs.OpenInNewWindow;
                SourceActionTooltip = "Open in Spotify";
                break;
            default:
                // No active provider — leave subtitle/chip blank.
                SubtitleLine = null;
                PathOrSecondary = null;
                SourceChipText = null;
                SourceChipImageUrl = null;
                SourceActionGlyph = FluentGlyphs.OpenInNewWindow;
                SourceActionTooltip = "Open source";
                ClearSpotifyVideoDetails();
                break;
        }

        // Audio vs video presentation. CurrentTrackIsVideo flips when the
        // engine resolves a music-video manifest; if it doesn't, we stay in
        // audio mode and render the album-art card instead of a black frame.
        IsAudioMode = ShouldUseAudioMode(effectiveKind);

        // Lyrics fetch — only for audio mode (videos already embed them).
        // Cleared first on every track change so a slow previous fetch
        // doesn't bleed into the new track.
        LyricsData = null;
        if (IsAudioMode)
            _ = LoadLyricsAsync();
        else
        {
            _lyricsVersion++;
            IsLyricsVisible = false;
        }

        UpdateSavedState();
    }

    private string? GetEffectiveActiveKind()
    {
        if (!string.IsNullOrEmpty(_surface.ActiveKind))
            return _surface.ActiveKind;

        if (!string.IsNullOrEmpty(GetCurrentOriginalSpotifyTrackUri())
            || !string.IsNullOrEmpty(GetCurrentSpotifyTrackUri()))
            return "spotify-audio";

        var trackId = _state?.CurrentTrackId;
        if (trackId?.StartsWith("wavee:local:", StringComparison.OrdinalIgnoreCase) == true)
            return "local";

        return ActiveKind;
    }

    private bool ShouldUseAudioMode(string? effectiveKind)
    {
        if (string.Equals(effectiveKind, "local", StringComparison.Ordinal))
            return !_surface.HasActiveSurface;

        if (effectiveKind?.StartsWith("spotify-", StringComparison.Ordinal) == true)
            return _state?.CurrentTrackIsVideo != true;

        return !_surface.HasActiveSurface && _state?.CurrentTrackIsVideo != true;
    }

    private async Task LoadLyricsAsync()
    {
        if (_lyricsService is null || _state is null) return;
        var trackId = _state.CurrentTrackId;
        if (string.IsNullOrEmpty(trackId)) return;

        var version = ++_lyricsVersion;
        var title = _state.CurrentTrackTitle;
        var artist = _state.CurrentArtistName;
        var durationMs = _state.Duration;
        var imageUrl = _state.CurrentAlbumArtLarge ?? _state.CurrentAlbumArt;

        try
        {
            var (lyrics, _) = await _lyricsService
                .GetLyricsForTrackAsync(trackId, title, artist, durationMs, imageUrl)
                .ConfigureAwait(false);
            if (_disposed || version != _lyricsVersion) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || version != _lyricsVersion) return;
                LyricsData = lyrics;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Lyrics fetch failed for {Track}", trackId);
        }
    }

    // Keep SaveLabelText in lockstep with IsSaved. Used by the labelled
    // "Save"/"Saved" pill in the redesigned hero action bar so the label
    // reflects the current state without each binding doing its own conversion.
    partial void OnIsSavedChanged(bool value)
    {
        SaveLabelText = value ? "Saved" : "Save";
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
        // Use the cached page theme — pre-fix this hard-coded `true` and
        // silently overwrote the light-theme palette tier on every track
        // change, even when Windows was in Light mode.
        ApplyPalette(_isDarkTheme);
        _ = LoadSpotifyVideoDetailsAsync();
    }

    private async Task LoadSpotifyVideoDetailsAsync()
    {
        var version = ++_spotifyDetailsVersion;
        var audioUri = GetCurrentOriginalSpotifyTrackUri() ?? GetCurrentSpotifyTrackUri();
        var artistUri = _state?.CurrentOriginalArtistId ?? _state?.CurrentArtistId;
        if (string.IsNullOrEmpty(audioUri))
        {
            _logger?.LogInformation("[video-page] LoadSpotifyVideoDetailsAsync skipped — no audioUri");
            ClearSpotifyVideoDetails();
            return;
        }
        _logger?.LogInformation(
            "[video-page] Loading details (track={Track}, artist={Artist}) — npv + metadata + watch-next",
            audioUri, artistUri ?? "<none>");

        // Reset statuses to Loading at the start of each fetch — the section
        // overlays show their spinners until the await chain lands.
        if (_dispatcher.HasThreadAccess)
        {
            RecommendedStatus = DataLoadStatus.Loading;
            VideosStatus = DataLoadStatus.Loading;
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                RecommendedStatus = DataLoadStatus.Loading;
                VideosStatus = DataLoadStatus.Loading;
            });
        }

        // Fire all three fetches independently — one failure (e.g. recommender
        // 404 for a regional/edge-case track) shouldn't blank the others. The
        // npv call is skipped when artistUri is missing (it's required by
        // that query); the recommender + metadata only need the track URI.
        var npvTask = SafeAwait(
            () => string.IsNullOrEmpty(artistUri) ? null : _pathfinder?.GetNpvArtistAsync(artistUri, audioUri),
            ex => _logger?.LogWarning(ex, "GetNpvArtistAsync failed for {Track}", audioUri));
        var trackTask = SafeAwait(
            () => _metadata?.GetTrackAsync(audioUri),
            ex => _logger?.LogWarning(ex, "GetTrackAsync failed for {Track}", audioUri));
        var watchNextTask = SafeAwait(
            () => _pathfinder?.GetSeoRecommendedTracksAsync(audioUri, 20),
            ex => _logger?.LogWarning(ex, "GetSeoRecommendedTracksAsync failed for {Track}", audioUri));
        // Playcount lives on getTrack — Track protobuf and npv don't carry it.
        var getTrackTask = SafeAwait(
            () => _pathfinder?.GetTrackAsync(audioUri),
            ex => _logger?.LogWarning(ex, "GetTrackAsync failed for {Track}", audioUri));

        await Task.WhenAll(npvTask, trackTask, watchNextTask, getTrackTask).ConfigureAwait(false);
        var (npv, npvFailed) = await npvTask.ConfigureAwait(false);
        var (videoTrack, _) = await trackTask.ConfigureAwait(false);
        var (watchNext, watchNextFailed) = await watchNextTask.ConfigureAwait(false);
        var (getTrack, _) = await getTrackTask.ConfigureAwait(false);

        if (_disposed || version != _spotifyDetailsVersion) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || version != _spotifyDetailsVersion) return;

            // Build a track-uri → formatted-playcount map from the artist's
            // top-tracks (returned by getTrack). Used to enrich both the
            // current-track playcount line *and* the Music videos rows
            // (npv RelatedVideos doesn't carry playcount, but the same
            // artist's top-tracks usually overlap with their videos).
            var topTracksPlays = BuildTopTracksPlaycountMap(getTrack);

            ApplySpotifyVideoDetails(npv, videoTrack, _state?.CurrentTrackIsVideo == true, topTracksPlays);
            ApplyWatchNext(watchNext, watchNextFailed);
            var plays = FormatPlayCountFull(getTrack?.Data?.TrackUnion?.Playcount);
            PlayCountText = string.IsNullOrEmpty(plays) ? null : plays;

            // Music-videos status mirrors npv outcome — npvFailed wins over
            // (artistUri missing) so a real error surfaces correctly.
            VideosStatus = npvFailed
                ? DataLoadStatus.Error
                : (HasRelatedVideos ? DataLoadStatus.Loaded : DataLoadStatus.Empty);
        });
    }

    private IReadOnlyDictionary<string, string> BuildTopTracksPlaycountMap(GetTrackResponse? response)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = response?.Data?.TrackUnion?.FirstArtist?.Items;
        if (items is null || items.Count == 0)
        {
            _logger?.LogInformation("[video-page] BuildTopTracksPlaycountMap: firstArtist.items={Count}", items?.Count ?? -1);
            return map;
        }
        foreach (var item in items)
        {
            var topTracks = item?.Discography?.TopTracks?.Items;
            if (topTracks is null) continue;
            foreach (var entry in topTracks)
            {
                var t = entry?.Track;
                if (string.IsNullOrEmpty(t?.Uri) || string.IsNullOrEmpty(t.Playcount)) continue;
                var formatted = FormatPlayCount(t.Playcount);
                if (!string.IsNullOrEmpty(formatted))
                    map[t.Uri] = formatted;
            }
        }
        _logger?.LogInformation("[video-page] BuildTopTracksPlaycountMap: {Count} top-track playcounts built", map.Count);
        return map;
    }

    /// <summary>
    /// Awaits a task factory and swallows the exception (logging it via
    /// <paramref name="onError"/>). Returns a tuple separating "result"
    /// from "failed" so the caller can distinguish a successful empty
    /// response from a thrown exception (drives the per-section error
    /// state on the right panel).
    /// </summary>
    private static async Task<(T? Result, bool Failed)> SafeAwait<T>(Func<Task<T>?> factory, Action<Exception> onError)
    {
        try
        {
            var task = factory();
            if (task is null) return (default, false);
            return (await task.ConfigureAwait(false), false);
        }
        catch (Exception ex)
        {
            onError(ex);
            return (default, true);
        }
    }

    private void ApplyWatchNext(SeoRecommendedTracksResponse? response, bool failed)
    {
        WatchNext.Clear();
        if (failed)
        {
            HasWatchNext = false;
            RecommendedStatus = DataLoadStatus.Error;
            _logger?.LogInformation("[video-page] ApplyWatchNext: error (fetch threw)");
            return;
        }

        // HasVideo defaults to false; resolved in a separate pass via
        // IMusicVideoMetadataService.EnsureAvailabilityAsync below — same
        // mechanism PlaylistViewModel uses (3-tier cache, batched).
        var items = response?.Data?.SeoRecommendedTrack?.Items;
        _logger?.LogInformation(
            "[video-page] ApplyWatchNext: response={HasResponse}, items={ItemCount}",
            response is not null, items?.Count ?? 0);
        if (items is { Count: > 0 })
        {
            foreach (var item in items)
            {
                var data = item?.Data;
                if (data is null || string.IsNullOrEmpty(data.Uri) || string.IsNullOrEmpty(data.Name))
                    continue;

                var artistsLine = data.Artists?.Items is { Count: > 0 } artists
                    ? string.Join(", ", artists
                        .Select(a => a.Profile?.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n)))
                    : "";

                var image = SmallestImageUrlAtLeast(data.AlbumOfTrack?.CoverArt?.Sources, 240);

                var durationText = FormatDuration(data.Duration?.TotalMilliseconds ?? 0);
                var playsText = FormatPlayCount(data.Playcount);
                var isExplicit = string.Equals(data.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase);

                // HasVideo starts false; the secondary pass below resolves
                // it via the music-video metadata cache.
                WatchNext.Add(new WatchNextItem(
                    data.Uri,
                    data.Name,
                    artistsLine,
                    image,
                    durationText,
                    playsText,
                    isExplicit,
                    hasVideo: false));
            }
        }
        HasWatchNext = WatchNext.Count > 0;
        RecommendedStatus = HasWatchNext ? DataLoadStatus.Loaded : DataLoadStatus.Empty;

        // Secondary pass: resolve which recommendations have music videos.
        // Same path PlaylistViewModel uses (cached → SQLite → ExtendedMetadata
        // batch). Not awaited — the rows render immediately, the badges
        // light up as availability lands.
        if (HasWatchNext)
            _ = ResolveWatchNextVideoAvailabilityAsync();
    }

    private async Task ResolveWatchNextVideoAvailabilityAsync()
    {
        if (_musicVideoMetadata is null || _disposed) return;
        var capturedVersion = _spotifyDetailsVersion;
        var uris = WatchNext.Select(w => w.TrackUri).Where(u => !string.IsNullOrEmpty(u)).ToList();
        if (uris.Count == 0) return;

        IReadOnlyDictionary<string, bool>? availability = null;
        try
        {
            availability = await _musicVideoMetadata.EnsureAvailabilityAsync(uris).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "EnsureAvailabilityAsync failed for watch-next batch");
            return;
        }

        if (_disposed || capturedVersion != _spotifyDetailsVersion) return;

        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || capturedVersion != _spotifyDetailsVersion) return;
            foreach (var item in WatchNext)
            {
                if (availability.TryGetValue(item.TrackUri, out var hasVideo))
                    item.HasVideo = hasVideo;
            }
        });
    }

    private void ApplySpotifyVideoDetails(NpvArtistResponse? npv, Track? videoTrack, bool isLinkedVideo, IReadOnlyDictionary<string, string>? topTracksPlaysMap = null)
    {
        var artist = npv?.Data?.ArtistUnion;
        var trackUnion = npv?.Data?.TrackUnion;

        SpotifyVideoTitle = videoTrack?.Name;
        SpotifyVideoImageUrl = GetAlbumImageUrl(videoTrack?.Album, Image.Types.Size.Large)
            ?? _state?.CurrentAlbumArtLarge
            ?? _state?.CurrentAlbumArt;

        // Derive album + artist URIs for the clickable description sub-cards.
        // Same pattern as TrackMetadataEnricher.cs:108 — convert the protobuf
        // Gid to base62 and prefix with the appropriate scheme. The cards are
        // visibility-gated on these URIs, so missing data simply hides them.
        if (videoTrack?.Album?.Gid is { Length: > 0 } albumGid)
            SpotifyAlbumUri = $"spotify:album:{SpotifyId.FromRaw(albumGid.Span, SpotifyIdType.Album).ToBase62()}";
        else
            SpotifyAlbumUri = null;
        _spotifyAlbumName = videoTrack?.Album?.Name;

        // Prefer the Pathfinder artist union URI (always canonical); fall back
        // to the playback state's cached artist id which is already a URI.
        SpotifyArtistUri = artist?.Uri
                          ?? _state?.CurrentOriginalArtistId
                          ?? _state?.CurrentArtistId;

        // "From the audio track" reads as a clearer affordance than the old
        // opaque "Original track metadata" badge for music videos with a
        // linked audio twin; "About this track" is the neutral fallback.
        TrackCardHeaderText = isLinkedVideo ? "From the audio track" : "About this track";

        // Reset the bio clamp on every new track so a previous "Read more"
        // doesn't bleed into the next artist's biography.
        SpotifyArtistBioExpanded = false;

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

        // Cleaned-up release line — pre-fix this dumped ISRC code, popularity
        // ranking, and "Live <date>" timestamps that nobody but the developer
        // cared about. Now just release date + label, both human-readable.
        SpotifyVideoReleaseLine = string.Join(" · ", new[]
        {
            FormatDate(videoTrack?.Album?.Date),
            videoTrack?.Album?.Label
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

        // "More videos by this artist" carousel. Each entry has a video URI
        // Music videos by this artist — drives the "Music videos" chip-tab
        // in the right panel. The SEO recommender (ApplyWatchNext) handles
        // the "Recommended" tab; these two lists serve different intents
        // (videos by THIS artist vs algorithmic recommendations) so we keep
        // both populated and let the user pick.
        RelatedVideos.Clear();
        var related = trackUnion?.RelatedVideos?.Items;
        var relatedVideoPlaycountHits = 0;
        if (related is { Count: > 0 })
        {
            foreach (var rv in related)
            {
                var data = rv?.TrackOfVideo?.Data;
                var audioUriForRv = data?.Uri;
                var videoUriForRv = rv?.Uri;
                if (string.IsNullOrEmpty(audioUriForRv) || string.IsNullOrEmpty(videoUriForRv))
                    continue;
                var name = data?.Name ?? "Video";
                var image = SmallestImageUrlAtLeast(data?.AlbumOfTrack?.CoverArt?.Sources, 240);
                // Look up playcount in the top-tracks map. Same-artist
                // videos almost always overlap with same-artist top tracks,
                // so the lookup hits in the common case. Empty string when
                // it doesn't (rare videos or live versions).
                var playsText = topTracksPlaysMap is not null
                    && topTracksPlaysMap.TryGetValue(audioUriForRv, out var p)
                    ? p : "";
                if (!string.IsNullOrEmpty(playsText))
                    relatedVideoPlaycountHits++;
                RelatedVideos.Add(new RelatedVideoItem(videoUriForRv, audioUriForRv, name, image, playsText));
            }
        }
        HasRelatedVideos = RelatedVideos.Count > 0;
        _logger?.LogInformation(
            "[video-page] Related videos built: rows={Rows}, playcountHits={Hits}, topTrackMap={MapCount}",
            RelatedVideos.Count,
            relatedVideoPlaycountHits,
            topTracksPlaysMap?.Count ?? 0);

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
        SpotifyAlbumUri = null;
        SpotifyArtistUri = null;
        _spotifyAlbumName = null;
        TrackCardHeaderText = "About this track";
        SpotifyArtistBioExpanded = false;
        IsStreamDetailsExpanded = false;
        WatchNext.Clear();
        HasWatchNext = false;
        RelatedVideos.Clear();
        HasRelatedVideos = false;
        RecommendedStatus = DataLoadStatus.Loading;
        VideosStatus = DataLoadStatus.Loading;
        SelectedRightPanelTab = RightPanelTabs[0];
        PlayCountText = null;
        LyricsData = null;
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

    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        ApplyPalette(isDark);
    }

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

        // Vertical top-to-bottom hero fade. Pre-fix this was horizontal
        // (EndPoint 1,0) which produced a hard right-edge cutoff on the page
        // backdrop. Vertical lets the backdrop bleed naturally into the page
        // body the way a true hero header reads. Stops keep their alpha curve
        // so the gradient self-fades to fully transparent at its bottom.
        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
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

    // Description-card sub-card commands. Both navigate via the shared
    // NavigationHelpers (same code path used by every other card click in the
    // app, so back-stack and tab-host behaviour stays identical).
    private void OpenArtist()
    {
        if (string.IsNullOrEmpty(SpotifyArtistUri)) return;
        NavigationHelpers.OpenArtist(SpotifyArtistUri, SourceChipText ?? "Artist");
    }

    private void OpenAlbum()
    {
        if (string.IsNullOrEmpty(SpotifyAlbumUri)) return;
        NavigationHelpers.OpenAlbum(SpotifyAlbumUri, _spotifyAlbumName ?? "Album");
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
        if (trackId.Contains(':') && !trackId.StartsWith("spotify:track:", StringComparison.Ordinal))
            return null;
        return trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? trackId
            : $"spotify:track:{trackId}";
    }

    private string? GetCurrentOriginalSpotifyTrackUri()
    {
        var trackId = _state?.CurrentOriginalTrackId;
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        if (trackId.Contains(':') && !trackId.StartsWith("spotify:track:", StringComparison.Ordinal))
            return null;
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

    /// <summary>
    /// Picks the smallest image source whose width is at least <paramref name="minWidth"/>,
    /// falling back to the largest available if none qualifies. Used for the
    /// 120 × 80 watch-next thumbnails — Spotify cover sources come in 64 / 300 / 640
    /// flavours, and 300 is the natural fit; this helper avoids loading 640 px JPEGs
    /// for a 120 px slot.
    /// </summary>
    private static string? SmallestImageUrlAtLeast(IReadOnlyList<ArtistImageSource>? sources, int minWidth)
    {
        if (sources is null || sources.Count == 0) return null;
        var ordered = sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .OrderBy(s => s.Width ?? s.MaxWidth ?? int.MaxValue)
            .ToArray();
        var fit = ordered.FirstOrDefault(s => (s.Width ?? s.MaxWidth ?? 0) >= minWidth);
        return fit?.Url ?? ordered.LastOrDefault()?.Url;
    }

    /// <summary>
    /// "1,234,567" → "1.2M plays", "12,345" → "12K plays", "0" → empty string.
    /// Spotify's GraphQL returns playcount as a string (it can exceed Int32),
    /// so this parses long and groups in human-readable buckets.
    /// </summary>
    private static string FormatPlayCount(string? playcount)
    {
        if (string.IsNullOrWhiteSpace(playcount)) return "";
        if (!long.TryParse(playcount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return "";
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000d:0.#}B plays";
        if (n >= 1_000_000) return $"{n / 1_000_000d:0.#}M plays";
        if (n >= 1_000) return $"{n / 1_000d:0.#}K plays";
        return $"{n} plays";
    }

    /// <summary>
    /// Like <see cref="FormatPlayCount"/> but emits the full number with
    /// thousands grouping (e.g. "2,419,830,517 plays"). Used for the
    /// prominent current-track playcount on the now-playing video page where
    /// the precise count is meaningful.
    /// </summary>
    private static string FormatPlayCountFull(string? playcount)
    {
        if (string.IsNullOrWhiteSpace(playcount)) return "";
        if (!long.TryParse(playcount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return "";
        return $"{n:N0} plays";
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

/// <summary>Status of an async-fetched right-panel section. Drives a 4-way
/// visual switch (spinner / list / empty state / error state) so the panel
/// never just looks broken when a Pathfinder call is in flight or fails.</summary>
public enum DataLoadStatus
{
    /// <summary>Fetch in flight — show spinner.</summary>
    Loading,
    /// <summary>Fetch completed; collection has items — render list.</summary>
    Loaded,
    /// <summary>Fetch completed; collection is empty — show empty state.</summary>
    Empty,
    /// <summary>Fetch failed (exception) — show error state.</summary>
    Error,
}

/// <summary>Right-panel chip-tab selection on the Now Playing video page.</summary>
public enum RightPanelTab
{
    /// <summary>SEO recommender — vertical "Up next"-style list.</summary>
    Recommended,
    /// <summary>npv-derived "Music videos by this artist" list.</summary>
    Videos,
    /// <summary>The real playback queue (QueueControl).</summary>
    Queue
}

/// <summary>One chip in the right-panel SessionTokenView. The
/// <see cref="Label"/> is what the user sees; <see cref="Tab"/> drives the
/// section visibility flags on the view-model.</summary>
/// <remarks>
/// <see cref="IsLoading"/> is a no-op stub so the SessionTokenItem template's
/// implicit IsLoading binding (used by other consumers — see
/// SessionControlChipViewModel — to drive the chase-around-border animation)
/// resolves cleanly. We never animate these chips, so it's hard-coded false.
/// </remarks>
public sealed record RightPanelTabChip(string Label, RightPanelTab Tab)
{
    public bool IsLoading => false;
}

/// <summary>Row in the YouTube-style "Recommended" vertical list in the right
/// panel. Backed by Spotify's <c>internalLinkRecommenderTrack</c> persisted
/// query (<see cref="SeoRecommendedTracksResponse"/>).</summary>
/// <param name="TrackUri">The <c>spotify:track:</c> URI to play on click.</param>
/// <param name="Title">Track title.</param>
/// <param name="ArtistsLine">Comma-joined artist names ("ROSÉ, Bruno Mars").</param>
/// <param name="ImageUrl">Cover-art thumbnail (~300 px source).</param>
/// <param name="DurationText">"2:34" / "1:23:45".</param>
/// <param name="PlaysText">"119M plays" / "4.2K plays" / empty if unknown.</param>
/// <param name="IsExplicit">True when contentRating == "EXPLICIT" — drives the small E badge.</param>
/// <remarks>
/// Class (not record) so <see cref="HasVideo"/> can be flipped in a second
/// pass after the row is added to the collection — the SEO recommender lands
/// first, then <see cref="IMusicVideoMetadataService.EnsureAvailabilityAsync"/>
/// resolves which of those tracks have music videos in Spotify's broader
/// catalog (not just by the current artist).
/// </remarks>
public sealed partial class WatchNextItem : ObservableObject
{
    public string TrackUri { get; }
    public string Title { get; }
    public string ArtistsLine { get; }
    public string? ImageUrl { get; }
    public string DurationText { get; }
    public string PlaysText { get; }
    public bool IsExplicit { get; }

    /// <summary>True when Spotify has a music video for this track. Mutates
    /// after the secondary EnsureAvailabilityAsync pass lands; UI listens
    /// to PropertyChanged so the badge appears once availability resolves.</summary>
    [ObservableProperty] private bool _hasVideo;

    public WatchNextItem(
        string trackUri,
        string title,
        string artistsLine,
        string? imageUrl,
        string durationText,
        string playsText,
        bool isExplicit,
        bool hasVideo)
    {
        TrackUri = trackUri;
        Title = title;
        ArtistsLine = artistsLine;
        ImageUrl = imageUrl;
        DurationText = durationText;
        PlaysText = playsText;
        IsExplicit = isExplicit;
        _hasVideo = hasVideo;
    }
}

/// <summary>Card in the "Music videos" tab — videos by the current artist
/// pulled from <c>npv.TrackUnion.RelatedVideos.Items</c>, enriched with
/// playcount from the parallel <c>getTrack</c> response when the video's
/// audio URI overlaps with the artist's top-tracks.</summary>
/// <param name="VideoUri">The <c>spotify:video:</c> URI to play.</param>
/// <param name="AudioTrackUri">The audio twin URI passed to the playback engine.</param>
/// <param name="Title">Track / video title.</param>
/// <param name="ImageUrl">Cover-art thumbnail.</param>
/// <param name="PlaysText">"123M plays" / "" when not in the artist's top-tracks map.</param>
public sealed record RelatedVideoItem(
    string VideoUri,
    string AudioTrackUri,
    string Title,
    string? ImageUrl,
    string PlaysText);
