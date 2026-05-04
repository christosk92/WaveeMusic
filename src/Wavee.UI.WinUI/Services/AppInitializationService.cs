using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.Contracts;
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
    private readonly IDataServiceConfiguration _config;
    private readonly ILogger? _logger;

    public AppInitializationService(
        IAuthState authState,
        IPlaybackStateService playbackStateService,
        INotificationService notificationService,
        IDataServiceConfiguration config,
        ILogger<AppInitializationService>? logger = null)
    {
        _authState = authState;
        _playbackStateService = playbackStateService;
        _notificationService = notificationService;
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

    private Task LoadDemoPlaybackAsync()
    {
        // Demo playback removed — app uses real Spotify data via IAlbumService
        return Task.CompletedTask;
    }
}
