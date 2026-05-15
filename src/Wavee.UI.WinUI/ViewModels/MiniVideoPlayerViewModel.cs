using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Backs the bottom-right floating <c>MiniVideoPlayer</c>. Source-agnostic;
/// reflects whatever the <see cref="IActiveVideoSurfaceService"/> reports as
/// active. Visibility is gated by:
///   <c>IsVideoActive == true &amp;&amp; !IsOnVideoPage</c>
/// — flipped from outside (<c>ShellViewModel</c> tracks page navigation).
/// </summary>
public sealed partial class MiniVideoPlayerViewModel : ObservableObject, IDisposable
{
    private readonly IActiveVideoSurfaceService _surface;
    private readonly IPlaybackStateService? _state;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private bool _disposed;
    private string? _lastTrackId;

    [ObservableProperty] private string? _title;
    [ObservableProperty] private bool _isVideoActive;
    [ObservableProperty] private bool _isOnVideoPage;
    [ObservableProperty] private bool _isSuppressedBySidebarPlayer;
    [ObservableProperty] private bool _isSuppressedByFloatingPlayer;
    [ObservableProperty] private bool _isDismissedByUser;

    /// <summary>
    /// True only once the active video surface has rendered at least one
    /// frame. Gates the mini-player's outer visibility so it never appears
    /// as a black box during the brief window between surface handoff and
    /// MediaFoundation's first-frame callback (the cause of the "lingering
    /// black surface on nav-away" symptom).
    /// </summary>
    [ObservableProperty] private bool _hasVideoSurfaceWithFirstFrame;

    public bool IsVisible => IsVideoActive
                             && HasVideoSurfaceWithFirstFrame
                             && !IsOnVideoPage
                             && !IsSuppressedBySidebarPlayer
                             && !IsSuppressedByFloatingPlayer
                             && !IsDismissedByUser;

    /// <summary>
    /// Mirror of <see cref="IsVisible"/> but inverted on
    /// <see cref="IsDismissedByUser"/>. When the user has clicked the X on
    /// Mini, the floating control hides — but the video keeps playing. The
    /// <c>VideoGripperView</c> in the shell shows itself in Mini's place so
    /// the user has a way to bring playback back into view.
    /// </summary>
    public bool IsGripperVisible => IsVideoActive
                                    && HasVideoSurfaceWithFirstFrame
                                    && !IsOnVideoPage
                                    && !IsSuppressedBySidebarPlayer
                                    && !IsSuppressedByFloatingPlayer
                                    && IsDismissedByUser;

    partial void OnHasVideoSurfaceWithFirstFrameChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    partial void OnIsVideoActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    partial void OnIsOnVideoPageChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    partial void OnIsSuppressedBySidebarPlayerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    partial void OnIsSuppressedByFloatingPlayerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    partial void OnIsDismissedByUserChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    /// <summary>
    /// Re-uses the singleton <see cref="PlayerBarViewModel"/> for transport
    /// commands (PlayPause / Next / Previous) and IsPlaying / IsBuffering
    /// state. The mini-player is a SECOND surface for the SAME control plane
    /// — no duplicate state machine, no risk of the two diverging.
    /// </summary>
    public PlayerBarViewModel? Player { get; }

    public IRelayCommand ExpandCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public MiniVideoPlayerViewModel(
        IActiveVideoSurfaceService surface,
        IPlaybackStateService? state,
        ILogger<MiniVideoPlayerViewModel>? logger = null)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _state = state;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException(
                          "MiniVideoPlayerViewModel must be constructed on the UI thread.");

        ExpandCommand = new RelayCommand(Expand);
        CloseCommand = new RelayCommand(Close);

        // Resolve PlayerBarViewModel once at ctor time. Singleton — guaranteed
        // present by the time this VM is constructed (also a singleton, both
        // via DI in AppLifecycleHelper).
        Player = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<PlayerBarViewModel>();

        IsVideoActive = _surface.HasActiveSurface;
        HasVideoSurfaceWithFirstFrame = _surface.HasActiveSurface && _surface.HasActiveFirstFrame;
        Title = _state?.CurrentTrackTitle;
        _lastTrackId = _state?.CurrentTrackId;

        _surface.ActiveSurfaceChanged += OnActiveSurfaceChanged;
        if (_state is not null)
            _state.PropertyChanged += OnStateChanged;
    }

    private void OnActiveSurfaceChanged(object? sender, MediaPlayer? surface)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnActiveSurfaceChanged(sender, surface));
            return;
        }
        var hasSurface = _surface.HasActiveSurface;
        if (!hasSurface || !IsVideoActive)
            IsDismissedByUser = false;

        IsVideoActive = hasSurface;
        // Re-evaluate the first-frame gate. ActiveVideoSurfaceService fires
        // ActiveSurfaceChanged on EVERY readiness flip (HasActiveFirstFrame,
        // IsActiveSurfaceLoading, IsActiveSurfaceBuffering — see the
        // service's OnProviderSurfaceChanged), so this handler is the
        // single hook needed to keep the gate in sync.
        HasVideoSurfaceWithFirstFrame = hasSurface && _surface.HasActiveFirstFrame;
        OnPropertyChanged(nameof(IsVisible));
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(IPlaybackStateService.CurrentTrackId))
        {
            void UpdateTrackDismissal()
            {
                var current = _state?.CurrentTrackId;
                if (!string.Equals(_lastTrackId, current, StringComparison.Ordinal))
                {
                    _lastTrackId = current;
                    IsDismissedByUser = false;
                    OnPropertyChanged(nameof(IsVisible));
                }
            }

            if (!_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(UpdateTrackDismissal);
            else
                UpdateTrackDismissal();
        }
        else if (e.PropertyName == nameof(IPlaybackStateService.CurrentTrackTitle))
        {
            if (!_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(() => Title = _state?.CurrentTrackTitle);
            else
                Title = _state?.CurrentTrackTitle;
        }
    }

    /// <summary>Called by <c>ShellViewModel</c> when navigation enters/leaves the video page.</summary>
    public void SetOnVideoPage(bool value)
    {
        if (IsOnVideoPage == value) return;
        IsOnVideoPage = value;
        OnPropertyChanged(nameof(IsVisible));
    }

    public void SetSuppressedBySidebarPlayer(bool value)
    {
        if (IsSuppressedBySidebarPlayer == value) return;
        IsSuppressedBySidebarPlayer = value;
        OnPropertyChanged(nameof(IsVisible));
    }

    public void SetSuppressedByFloatingPlayer(bool value)
    {
        if (IsSuppressedByFloatingPlayer == value) return;
        IsSuppressedByFloatingPlayer = value;
        OnPropertyChanged(nameof(IsVisible));
    }

    public void ShowByUserRequest()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(ShowByUserRequest);
            return;
        }

        IsDismissedByUser = false;
        IsSuppressedBySidebarPlayer = false;
        IsVideoActive = _surface.HasActiveSurface;
        HasVideoSurfaceWithFirstFrame = _surface.HasActiveSurface && _surface.HasActiveFirstFrame;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsGripperVisible));
    }

    /// <summary>
    /// Called by <c>VideoGripperView</c> when the user clicks the
    /// right-edge tab. Resets <see cref="IsDismissedByUser"/> so the floating
    /// Mini-player reappears in place. Just an alias for
    /// <see cref="ShowByUserRequest"/> — kept distinct so the call sites read
    /// naturally.
    /// </summary>
    public void Restore() => ShowByUserRequest();

    private void Expand()
    {
        IsDismissedByUser = false;
        OnPropertyChanged(nameof(IsVisible));

        // Same call the auto-nav uses on play start — opens (or focuses) the
        // VideoPlayerPage. The page's OnNavigatedTo will reclaim the surface
        // through IActiveVideoSurfaceService and the mini's DetachSurface
        // fires automatically.
        try { Helpers.Navigation.NavigationHelpers.OpenVideoPlayer(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Mini-player expand failed"); }
    }

    private void Close()
    {
        // UI-only dismiss; playback keeps running.
        IsDismissedByUser = true;
        OnPropertyChanged(nameof(IsVisible));
    }

    public void Show()
    {
        ShowByUserRequest();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.ActiveSurfaceChanged -= OnActiveSurfaceChanged;
        if (_state is not null) _state.PropertyChanged -= OnStateChanged;
    }
}
