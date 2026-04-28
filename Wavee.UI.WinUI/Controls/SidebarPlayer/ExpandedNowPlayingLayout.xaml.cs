using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Apple-Music-style "now playing" left column for the floating player's
/// expanded layout. Reuses the singleton <see cref="PlayerBarViewModel"/> as
/// the data source and the small primitive controls (<c>HeartButton</c>,
/// <c>OutputDevicePicker</c>, <c>CompositionProgressBar</c>,
/// <c>PlaybackActionContent</c>) — no parallel transport implementation.
/// </summary>
public sealed partial class ExpandedNowPlayingLayout : UserControl
{
    public PlayerBarViewModel ViewModel { get; }
    private readonly ITrackLikeService? _likeService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly ILogger<ExpandedNowPlayingLayout>? _logger;

    public ExpandedNowPlayingLayout()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _likeService = Ioc.Default.GetService<ITrackLikeService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<ExpandedNowPlayingLayout>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        HeartButton.Command = new RelayCommand(OnHeartClicked);

        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateHeartState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => ViewModel.SetSurfaceVisible("widget", true);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetSurfaceVisible("widget", false);
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_likeService != null) _likeService.SaveStateChanged -= OnSaveStateChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerBarViewModel.HasTrack) or nameof(PlayerBarViewModel.TrackTitle))
            UpdateHeartState();
    }

    // ── Progress seek ──────────────────────────────────────────────────────

    private void ProgressBar_SeekStarted(object sender, System.EventArgs e)
        => ViewModel.StartSeeking();

    private void ProgressBar_SeekCommitted(object sender, double positionMs)
        => ViewModel.CommitSeekFromBar(positionMs);

    // ── Heart state (mirrors SidebarPlayerWidget pattern) ─────────────────

    private string? GetCurrentTrackId() => _playbackStateService?.CurrentTrackId;

    private void OnSaveStateChanged()
        => DispatcherQueue?.TryEnqueue(UpdateHeartState);

    private void UpdateHeartState()
    {
        var trackId = GetCurrentTrackId();
        var isLiked = !string.IsNullOrEmpty(trackId)
            && _likeService?.IsSaved(SavedItemType.Track, trackId) == true;
        HeartButton.IsLiked = isLiked;
    }

    private void OnHeartClicked()
    {
        var trackId = GetCurrentTrackId();
        if (string.IsNullOrEmpty(trackId) || _likeService == null) return;

        var uri = $"spotify:track:{trackId}";
        var wasLiked = HeartButton.IsLiked;
        _logger?.LogInformation("[ExpandedNowPlaying] Heart clicked: trackId={TrackId}, wasLiked={WasLiked}", trackId, wasLiked);
        _likeService.ToggleSave(SavedItemType.Track, uri, wasLiked);
        HeartButton.IsLiked = !wasLiked;
    }
}
