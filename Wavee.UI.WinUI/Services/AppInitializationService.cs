using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Orchestrates app initialization in demo mode: restores auth, loads a demo
/// playback queue from mock catalog data, and shows a welcome notification.
/// </summary>
internal sealed class AppInitializationService
{
    private readonly IAuthState _authState;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly INotificationService _notificationService;
    private readonly ICatalogService _catalogService;
    private readonly IDataServiceConfiguration _config;
    private readonly ILogger? _logger;

    public AppInitializationService(
        IAuthState authState,
        IPlaybackStateService playbackStateService,
        INotificationService notificationService,
        ICatalogService catalogService,
        IDataServiceConfiguration config,
        ILogger<AppInitializationService>? logger = null)
    {
        _authState = authState;
        _playbackStateService = playbackStateService;
        _notificationService = notificationService;
        _catalogService = catalogService;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Initializes auth and playback state. Call BEFORE shell navigation
    /// so PlayerBar reads populated data on construction.
    /// </summary>
    public async Task InitializeStateAsync()
    {
        if (!_config.IsDemoMode)
            return;

        try
        {
            // 1. Restore auth (sets demo_user, Premium, Authenticated)
            await _authState.TryRestoreSessionAsync();

            // 2. Load demo album into playback queue
            await LoadDemoPlaybackAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "App state initialization failed");
        }
    }

    /// <summary>
    /// Shows a welcome notification. Call AFTER shell navigation
    /// so ShellViewModel is already listening.
    /// </summary>
    public void ShowWelcomeNotification()
    {
        if (!_config.IsDemoMode)
            return;

        _notificationService.Show(
            "Welcome to Wavee! You're running in demo mode.",
            NotificationSeverity.Informational,
            autoDismissAfter: TimeSpan.FromSeconds(5));
    }

    private async Task LoadDemoPlaybackAsync()
    {
        const string demoAlbumId = "spotify:album:1"; // Abbey Road

        var album = await _catalogService.GetAlbumAsync(demoAlbumId);
        var tracks = await _catalogService.GetAlbumTracksAsync(demoAlbumId);

        if (tracks.Count == 0)
            return;

        var queueItems = tracks.Select(t => new QueueItem
        {
            TrackId = t.Id,
            Title = t.Title,
            ArtistName = t.ArtistName,
            AlbumArt = t.ImageUrl ?? album.ImageUrl,
            DurationMs = t.Duration.TotalMilliseconds,
            IsUserQueued = false
        }).ToList();

        var context = new PlaybackContextInfo
        {
            ContextUri = demoAlbumId,
            Type = PlaybackContextType.Album,
            Name = album.Name,
            ImageUrl = album.ImageUrl
        };

        _playbackStateService.LoadQueue(queueItems, context, startIndex: 0);
    }
}
