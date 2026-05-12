using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Cinematic, video-first "Now playing" page. Replaces the previous
/// wrapper around <c>ExpandedPlayerView</c> — that wrapper inherited a
/// surface-ownership gate inside <c>ExpandedNowPlayingLayout</c> that left
/// the page showing the embedded poster image instead of the live video
/// frame (the gate combined <c>_canTakeVideoSurfaceFromVideoPage</c> with
/// <c>_miniVideoViewModel.IsOnVideoPage</c> and never let the page itself
/// acquire the surface).
///
/// <para>This page acquires the active video surface directly via
/// <see cref="IActiveVideoSurfaceService"/>, mirroring the pattern used by
/// the working <c>MiniVideoPlayer</c>, and lays out a single fading scrim
/// over the video. Queue, lyrics, and details remain owned by the shell
/// right panel rather than an inline dock.</para>
/// </summary>
public sealed partial class VideoPlayerPage : Page, IMediaSurfaceConsumer
{
    private const int ScrimIdleHideMs = 1500;
    // Hard cap on how long the hover-pin can keep the chrome alive without
    // any pointer activity. Catches the case where a pointer enters the
    // scrim and then the cursor moves to another monitor (or the app loses
    // focus) — Windows doesn't fire PointerExited in that situation, so
    // without this cap the chrome stays visible forever.
    private const int MaxScrimPinMs = 4000;
    private const int FadeDurationMs = 200;

    private readonly IActiveVideoSurfaceService _surface;
    private readonly ITrackLikeService? _likeService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public PlayerBarViewModel ViewModel { get; }

    private MediaPlayerElement? _videoElement;
    private FrameworkElement? _videoElementSurface;

    private DispatcherQueueTimer? _hideTimer;
    private Storyboard? _scrimFadeStoryboard;
    private bool _scrimVisible = true;
    private bool _scrimPinned;

    private int _heartStateVersion;
    private bool _eventsSubscribed;
    private bool _appWindowSubscribed;
    private bool _windowActivatedSubscribed;

    public VideoPlayerPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _surface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        _likeService = Ioc.Default.GetService<ITrackLikeService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
        _logger = Ioc.Default.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ?.CreateLogger<VideoPlayerPage>();

        InitializeComponent();

        HeartButtonCtrl.Command = new RelayCommand(OnHeartClicked);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Pointer hooks: any movement inside the page wakes the scrim. The
        // scrim itself pins visibility while hovered so a stationary cursor
        // over the transport row doesn't fade out under the user.
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnPagePointerMoved), handledEventsToo: true);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerMoved), handledEventsToo: true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), handledEventsToo: true);

        Scrim.PointerEntered += OnScrimPointerEntered;
        Scrim.PointerExited += OnScrimPointerExited;
        Scrim.GotFocus += OnScrimGotFocus;
        Scrim.LostFocus += OnScrimLostFocus;
    }

    // ── Navigation lifecycle ────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation(
            "[VideoPlayerPage.OnLoaded] instance={Hash} hostedInTheatreFrame={InTheatre}",
            GetHashCode(),
            Frame is { } f && f.Name == "TheatreFrame");
        EnsureHideTimer();
        SubscribeEvents();

        _surface.AcquireSurface(this);

        ApplyVideoStatusOverlay();
        ApplyListenAsAudioVisibility();
        UpdateHeartState();

        // First paint: bring chrome up, then start the idle timer. Without
        // this initial show the user lands on a black-then-fade transition
        // that feels janky.
        FadeScrim(visible: true);
        RestartHideTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _logger?.LogInformation(
            "[VideoPlayerPage.OnUnloaded] instance={Hash} ownsSurface={Owns}",
            GetHashCode(), _surface.IsOwnedBy(this));
        UnsubscribeEvents();

        // Defensive — always restore the cursor on page teardown. ShowCursor
        // is a per-thread counter; an unmatched HideCursor would leave the
        // entire window's cursor invisible.
        RestoreCursor();

        _surface.ReleaseSurface(this);

        if (_hideTimer is not null)
        {
            _hideTimer.Stop();
            _hideTimer.Tick -= OnHideTimerTick;
            _hideTimer = null;
        }
        _scrimFadeStoryboard?.Stop();
        _scrimFadeStoryboard = null;
    }

    // ── Event wiring ───────────────────────────────────────────────────────

    private void SubscribeEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        _surface.ActiveSurfaceChanged += OnActiveSurfaceChanged;
        _surface.SurfaceOwnershipChanged += OnSurfaceOwnershipChanged;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        if (_likeService != null)
            _likeService.SaveStateChanged += OnLikeStateChanged;

        if (_playbackStateService is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += OnPlaybackStateChanged;

        var appWindow = MainWindow.Instance?.AppWindow;
        if (appWindow is not null && !_appWindowSubscribed)
        {
            appWindow.Changed += OnAppWindowChanged;
            _appWindowSubscribed = true;
        }

        // Force-hide chrome when the window deactivates — covers the case
        // where the user moves the cursor to another monitor / clicks
        // another app. PointerExited doesn't fire in those flows, so without
        // this hook a scrim pinned by hover would stay alive in the
        // background.
        var window = MainWindow.Instance;
        if (window is not null && !_windowActivatedSubscribed)
        {
            window.Activated += OnWindowActivatedForScrim;
            _windowActivatedSubscribed = true;
        }

        // The presentation service is the source of truth for the expand
        // DropDownButton's glyph + the Theatre / Fullscreen menu-item check
        // states. Sync once on load so the chrome reflects whatever mode
        // we're in when the page mounts.
        _presentationService = Ioc.Default.GetService<INowPlayingPresentationService>();
        if (_presentationService is not null)
            _presentationService.PropertyChanged += OnPresentationServicePropertyChanged;
    }

    private void UnsubscribeEvents()
    {
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;

        _surface.ActiveSurfaceChanged -= OnActiveSurfaceChanged;
        _surface.SurfaceOwnershipChanged -= OnSurfaceOwnershipChanged;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_likeService != null)
            _likeService.SaveStateChanged -= OnLikeStateChanged;

        if (_playbackStateService is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= OnPlaybackStateChanged;

        var appWindow = MainWindow.Instance?.AppWindow;
        if (appWindow is not null && _appWindowSubscribed)
        {
            appWindow.Changed -= OnAppWindowChanged;
            _appWindowSubscribed = false;
        }

        var window = MainWindow.Instance;
        if (window is not null && _windowActivatedSubscribed)
        {
            window.Activated -= OnWindowActivatedForScrim;
            _windowActivatedSubscribed = false;
        }

        if (_presentationService is not null)
        {
            _presentationService.PropertyChanged -= OnPresentationServicePropertyChanged;
            _presentationService = null;
        }
    }

    private INowPlayingPresentationService? _presentationService;

    private void OnPresentationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INowPlayingPresentationService.Presentation))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                var hostFrame = Frame;
                var hostFrameName = hostFrame?.Name ?? "<no-frame>";
                var isInTheatreFrame = string.Equals(hostFrameName, "TheatreFrame", StringComparison.Ordinal);
                _logger?.LogInformation(
                    "[VideoPlayerPage.OnPresentation] instance={Hash} presentation={Presentation} isLoaded={IsLoaded} ownsSurface={Owns} hostFrame={Frame}",
                    GetHashCode(),
                    _presentationService?.Presentation,
                    IsLoaded,
                    _surface.IsOwnedBy(this),
                    hostFrameName);

                // Always restore the cursor when leaving an expanded
                // mode, regardless of where this instance lives. The
                // FadeScrim path covers most cases but a presentation
                // exit while the scrim was already hidden would otherwise
                // leave the cursor invisible.
                if (_presentationService is { IsNormal: true })
                    RestoreCursor();

                // When Theatre/Fullscreen exits, the TheatreFrame's
                // VideoPlayerPage instance is about to be torn down (its
                // host frame's Content was just set to null). Its
                // presentation handler still fires because the event
                // subscription only drops on Unloaded — but it MUST NOT
                // re-acquire the surface because the host Frame is
                // collapsed (size 0×0) and any attach there is wasted, AND
                // its imminent Unload would then release the surface
                // leaving no owner at all. The tab's instance is the only
                // one that should reclaim ownership on presentation→Normal.
                if (isInTheatreFrame)
                {
                    _logger?.LogDebug(
                        "[VideoPlayerPage] skip re-acquire — TheatreFrame instance is being torn down (instance={Hash})",
                        GetHashCode());
                    return;
                }

                if (_presentationService is { IsNormal: true }
                    && IsLoaded
                    && !_surface.IsOwnedBy(this))
                {
                    _logger?.LogInformation(
                        "[VideoPlayerPage] re-acquiring surface after presentation→Normal (instance={Hash}, tab)",
                        GetHashCode());
                    try { _surface.AcquireSurface(this); }
                    catch (Exception ex) { _logger?.LogDebug(ex, "AcquireSurface threw on presentation change"); }
                }
            });
        }
    }

    // ── Keyboard accelerators (F11 / Esc) ────────────────────────────────
    //
    // ShellPage.OnProcessKeyboardAccelerators also handles F11/Esc, but those
    // only fire when ShellPage is the focused page. When the user enters
    // Theatre/Fullscreen, the TheatreFrame's VideoPlayerPage becomes the
    // focused page, so this Page override is the one that catches the keys.
    // Same routing as ShellPage — straight through the presentation service.
    protected override void OnProcessKeyboardAccelerators(ProcessKeyboardAcceleratorEventArgs args)
    {
        if (args.Modifiers == Windows.System.VirtualKeyModifiers.None)
        {
            if (args.Key == Windows.System.VirtualKey.F11)
            {
                (_presentationService ??= Ioc.Default.GetService<INowPlayingPresentationService>())
                    ?.ToggleFullscreen();
                args.Handled = true;
                return;
            }
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                var svc = _presentationService ??= Ioc.Default.GetService<INowPlayingPresentationService>();
                if (svc is { IsExpanded: true })
                {
                    svc.ExitToNormal();
                    args.Handled = true;
                    return;
                }
            }

            // VLC-style short seek — Left/Right arrow nudge a few seconds.
            // Same as the < and > characters share the keys on most layouts
            // so the user's "couple of frames" intuition maps here without
            // needing to know the codepoint.
            if (args.Key == Windows.System.VirtualKey.Left)
            {
                ViewModel.SeekByMilliseconds(-5_000);
                FadeScrim(visible: true);
                RestartHideTimer();
                args.Handled = true;
                return;
            }
            if (args.Key == Windows.System.VirtualKey.Right)
            {
                ViewModel.SeekByMilliseconds(5_000);
                FadeScrim(visible: true);
                RestartHideTimer();
                args.Handled = true;
                return;
            }

            // Spacebar = play/pause — matches every video player.
            if (args.Key == Windows.System.VirtualKey.Space)
            {
                ViewModel.PlayPauseCommand.Execute(null);
                args.Handled = true;
                return;
            }
        }
        else if (args.Modifiers == Windows.System.VirtualKeyModifiers.Shift)
        {
            // Shift+Left → −10s (matches the SkipBack10 scrim button).
            // Shift+Right → +30s (matches the SkipForward30 button).
            // Asymmetric on purpose: rewind small, fast-forward big — same
            // as VLC's default and the Fluent ED3C/ED3D glyphs imply.
            if (args.Key == Windows.System.VirtualKey.Left)
            {
                ViewModel.SkipBack10Command.Execute(null);
                FadeScrim(visible: true);
                RestartHideTimer();
                args.Handled = true;
                return;
            }
            if (args.Key == Windows.System.VirtualKey.Right)
            {
                ViewModel.SkipForward30Command.Execute(null);
                FadeScrim(visible: true);
                RestartHideTimer();
                args.Handled = true;
                return;
            }
        }

        base.OnProcessKeyboardAccelerators(args);
    }

    // Theatre / Fullscreen / Mini-player / Pop-out menu items moved into the
    // shared VideoTransportBar UserControl. The bar resolves the presentation
    // service + docking service itself, so the page no longer holds these
    // event handlers.

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.IsCurrentTrackAudioCapable):
                ApplyListenAsAudioVisibility();
                break;
        }
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Heart state needs to refresh whenever the playing track changes —
        // PlayerBarViewModel doesn't directly track liked-ness, the like
        // service is the source of truth. Mirrors PlayerBar.xaml.cs.
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackManifestId)
            or "CurrentTrackUri"
            or "CurrentTrackHasMusicVideo")
        {
            DispatcherQueue?.TryEnqueue(UpdateHeartState);
        }
    }

    private void OnLikeStateChanged()
        => DispatcherQueue?.TryEnqueue(UpdateHeartState);

    // ── IMediaSurfaceConsumer ─────────────────────────────────────────────

    public void AttachSurface(MediaPlayer player)
    {
        _logger?.LogInformation(
            "[VideoPlayerPage.AttachSurface] instance={Hash} isLoaded={IsLoaded} hadElement={HadElement} hostSize={W}x{H}",
            GetHashCode(), IsLoaded, _videoElement is not null,
            VideoHost.ActualWidth, VideoHost.ActualHeight);
        DetachElementSurfaceInternal();
        if (_videoElement is null)
        {
            _videoElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsTabStop = false,
                IsHitTestVisible = false, // pointer events bubble to the scrim
            };
            VideoHost.Children.Insert(0, _videoElement);
            // Force layout so MediaFoundation sees a non-zero render target
            // BEFORE the SetMediaPlayer below — without this, a freshly-
            // created element occasionally renders black because the swap
            // chain is sized to 0x0 at attach time and MediaFoundation never
            // re-allocates after the first frame arrives. UpdateLayout flushes
            // the synchronous measure / arrange pass.
            _videoElement.UpdateLayout();
            _logger?.LogDebug(
                "[VideoPlayerPage.AttachSurface] created fresh MediaPlayerElement, post-layout size={W}x{H}",
                _videoElement.ActualWidth, _videoElement.ActualHeight);
        }
        _videoElement.SetMediaPlayer(player);

        // Nudge MediaFoundation to push the *current* frame to the new render
        // target. SetMediaPlayer re-binds the player but doesn't request a
        // frame redraw; if playback was already running on the previous
        // element, the new element waits up to a frame interval for the next
        // produced frame, which manifests as a black surface during the wait.
        // Setting Position = Position is a no-op seek that triggers the
        // engine to re-emit the current frame immediately.
        try
        {
            var session = player.PlaybackSession;
            if (session.CanSeek)
                session.Position = session.Position;
        }
        catch
        {
            // Best-effort; MediaFoundation is robust to a missed nudge.
        }

        ApplyVideoStatusOverlay();
    }

    public void AttachElementSurface(FrameworkElement element)
    {
        DetachMediaPlayerSurfaceInternal();
        if (_videoElementSurface is not null && ReferenceEquals(_videoElementSurface, element))
            return;

        _videoElementSurface = element;
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.IsHitTestVisible = false;
        VideoHost.Children.Insert(0, element);
        ApplyVideoStatusOverlay();
    }

    public void DetachSurface()
    {
        _logger?.LogInformation(
            "[VideoPlayerPage.DetachSurface] instance={Hash} hadElement={HadElement} hadElementSurface={HadElementSurface} isLoaded={IsLoaded}",
            GetHashCode(), _videoElement is not null, _videoElementSurface is not null, IsLoaded);
        DetachMediaPlayerSurfaceInternal();
        DetachElementSurfaceInternal();
        ApplyVideoStatusOverlay();
    }

    private void DetachMediaPlayerSurfaceInternal()
    {
        if (_videoElement is null) return;
        _videoElement.SetMediaPlayer(null);
        VideoHost.Children.Remove(_videoElement);
        _videoElement = null;
        _logger?.LogDebug("[VideoPlayerPage] MediaPlayerElement removed from VideoHost");
    }

    private void DetachElementSurfaceInternal()
    {
        if (_videoElementSurface is null) return;
        VideoHost.Children.Remove(_videoElementSurface);
        _videoElementSurface.IsHitTestVisible = true;
        _videoElementSurface = null;
        _logger?.LogDebug("[VideoPlayerPage] element surface removed from VideoHost");
    }

    private void OnActiveSurfaceChanged(object? sender, MediaPlayer? surface)
        => DispatcherQueue?.TryEnqueue(ApplyVideoStatusOverlay);

    private void OnSurfaceOwnershipChanged(object? sender, EventArgs e)
        => DispatcherQueue?.TryEnqueue(ApplyVideoStatusOverlay);

    private void ApplyVideoStatusOverlay()
    {
        var hasAttachedVideo = _videoElement is not null || _videoElementSurface is not null;
        var showLoading = hasAttachedVideo
                          && _surface.HasActiveSurface
                          && !_surface.HasActiveFirstFrame;
        var showBuffering = hasAttachedVideo
                            && _surface.HasActiveSurface
                            && _surface.HasActiveFirstFrame
                            && _surface.IsActiveSurfaceBuffering;

        VideoStatusText.Text = showBuffering ? "Buffering" : "Loading";
        VideoStatusOverlay.Visibility = (showLoading || showBuffering)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Auto-fade scrim ────────────────────────────────────────────────────

    private void EnsureHideTimer()
    {
        if (_hideTimer is not null) return;
        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq is null) return;
        _hideTimer = dq.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(ScrimIdleHideMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += OnHideTimerTick;
    }

    private DateTime _scrimPinnedAt = DateTime.MinValue;
    private bool _cursorHidden;

    // Win32 cursor visibility — WinUI's InputCursor has no "invisible" preset,
    // and a transparent InputDesktopResourceCursor requires shipping a .cur
    // file. ShowCursor decrements a per-thread visibility counter; balance
    // each (false) with a matching (true) so the cursor count never drifts
    // out of zero/one range. The counter is process-thread scoped so this
    // doesn't bleed into other windows.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private void HideCursor()
    {
        if (_cursorHidden) return;
        ShowCursor(false);
        _cursorHidden = true;
    }

    private void RestoreCursor()
    {
        if (!_cursorHidden) return;
        ShowCursor(true);
        _cursorHidden = false;
    }

    private void OnHideTimerTick(DispatcherQueueTimer sender, object args)
    {
        // Even when the user is hovering the scrim, force a hide once the
        // pin has been held longer than MaxScrimPinMs. Catches the case
        // where the pointer leaves the window to another monitor or the
        // app loses focus — neither path fires PointerExited reliably, so
        // without this fallback the chrome stays visible indefinitely.
        if (_scrimPinned)
        {
            var pinnedFor = (DateTime.UtcNow - _scrimPinnedAt).TotalMilliseconds;
            if (pinnedFor < MaxScrimPinMs)
            {
                // Re-schedule a check for the remaining window.
                _hideTimer?.Stop();
                _hideTimer!.Interval = TimeSpan.FromMilliseconds(
                    Math.Max(250, MaxScrimPinMs - pinnedFor));
                _hideTimer.Start();
                return;
            }
        }

        FadeScrim(visible: false);
    }

    private void RestartHideTimer()
    {
        // Always restart — even when pinned. The OnHideTimerTick decides
        // whether to force-hide based on how long the pin has been held.
        _hideTimer?.Stop();
        if (_hideTimer is not null)
        {
            _hideTimer.Interval = TimeSpan.FromMilliseconds(ScrimIdleHideMs);
            _hideTimer.Start();
        }
    }

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Any movement on the page counts as activity — clear the pin too
        // so a stale "pinned" state can't outlive the hover that set it.
        if (_scrimPinned && IsPointerOutsideScrim(e))
        {
            _scrimPinned = false;
            _scrimPinnedAt = DateTime.MinValue;
        }
        FadeScrim(visible: true);
        RestartHideTimer();
    }

    private static bool IsPointerOutsideScrim(PointerRoutedEventArgs e)
    {
        // PointerMoved on the page fires regardless of where the cursor is —
        // we only want to clear the pin when the cursor has actually left
        // the scrim's bounds. The scrim's PointerExited handler covers the
        // normal case; this is the redundancy for multi-monitor edge cases.
        return e.OriginalSource is FrameworkElement fe
            && fe is not Microsoft.UI.Xaml.Controls.Grid { Name: "Scrim" };
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        FadeScrim(visible: true);
        RestartHideTimer();
    }

    private void OnScrimPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _scrimPinned = true;
        _scrimPinnedAt = DateTime.UtcNow;
        // Don't stop the timer — let it tick and force-hide if the pin
        // exceeds MaxScrimPinMs. Re-schedule for the cap.
        _hideTimer?.Stop();
        if (_hideTimer is not null)
        {
            _hideTimer.Interval = TimeSpan.FromMilliseconds(MaxScrimPinMs);
            _hideTimer.Start();
        }
        FadeScrim(visible: true);
    }

    private void OnScrimPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _scrimPinned = false;
        _scrimPinnedAt = DateTime.MinValue;
        RestartHideTimer();
    }

    private void OnScrimGotFocus(object sender, RoutedEventArgs e)
    {
        _scrimPinned = true;
        _scrimPinnedAt = DateTime.UtcNow;
        _hideTimer?.Stop();
        if (_hideTimer is not null)
        {
            _hideTimer.Interval = TimeSpan.FromMilliseconds(MaxScrimPinMs);
            _hideTimer.Start();
        }
        FadeScrim(visible: true);
    }

    private void OnScrimLostFocus(object sender, RoutedEventArgs e)
    {
        _scrimPinned = false;
        _scrimPinnedAt = DateTime.MinValue;
        RestartHideTimer();
    }

    private void OnWindowActivatedForScrim(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Force-hide regardless of pin state — the user clearly isn't
            // interacting with this window anymore. Cheap; calling
            // FadeScrim(false) when already hidden is a no-op.
            _scrimPinned = false;
            _scrimPinnedAt = DateTime.MinValue;
            FadeScrim(visible: false);
        }
    }

    private void FadeScrim(bool visible)
    {
        // Cursor sync runs OUTSIDE the early-return guard below — pointer
        // movement always restores the cursor even when the scrim was
        // already visible (no opacity change needed), and idle ticks always
        // re-hide it even when the scrim was already invisible. Keeps the
        // cursor in lockstep with the chrome regardless of dedup.
        if (_presentationService is { IsExpanded: true })
        {
            if (visible) RestoreCursor(); else HideCursor();
        }
        else
        {
            // Normal presentation never hides the cursor — restore if a
            // previous expanded-mode left it hidden.
            RestoreCursor();
        }

        if (_scrimVisible == visible && Scrim.Opacity == (visible ? 1 : 0)) return;
        _scrimVisible = visible;

        _scrimFadeStoryboard?.Stop();
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = visible ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(FadeDurationMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, Scrim);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        _scrimFadeStoryboard = sb;
        sb.Begin();

        // Disable hit-testing on the chrome while invisible so a stray click
        // does not hit an invisible transport control.
        Scrim.IsHitTestVisible = visible;
    }

    // ── Fullscreen toggle ──────────────────────────────────────────────────
    //
    // Drives the main window's AppWindow presenter between FullScreen and
    // Overlapped (same approach the popout window uses, see
    // PlayerFloatingWindow.xaml.cs::EnterFullScreen/ExitFullScreen). We
    // subscribe to AppWindow.Changed so user-initiated transitions
    // (F11 / Esc / system) keep the toggle's IsChecked in sync.

    // Fullscreen / Theatre / shuffle / repeat visuals moved into the shared
    // VideoTransportBar UserControl. The page subscribes to AppWindow
    // changes only for cursor-restoration timing now; the bar handles its
    // own glyph swaps via INowPlayingPresentationService.

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // No-op for chrome sync (VideoTransportBar owns that) — kept as the
        // subscription hook in case future AppWindow state changes need to
        // drive the cursor / scrim behavior.
    }

    private void ApplyListenAsAudioVisibility()
    {
        ListenAsAudioButton.Visibility = ViewModel.IsCurrentTrackAudioCapable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TrackTitle_Click(object sender, RoutedEventArgs e)
        => NavigateToCurrentTitleTarget();

    private void ArtistLine_Click(object sender, RoutedEventArgs e)
        => NavigateToCurrentSubtitleTarget();

    private void NavigateToCurrentTitleTarget()
    {
        if (PodcastPlaybackNavigation.TryOpenCurrentEpisode(
                _playbackStateService,
                ViewModel.TrackTitle,
                ViewModel.AlbumArtLarge ?? ViewModel.AlbumArt,
                ViewModel.ArtistName,
                ViewModel.AlbumArtLarge ?? ViewModel.AlbumArt))
        {
            return;
        }

        if (TryOpenLocalContentTarget())
            return;

        var albumId = ViewModel.CurrentAlbumId;
        if (string.IsNullOrWhiteSpace(albumId)) return;

        var param = new ContentNavigationParameter
        {
            Uri = albumId,
            Title = ViewModel.TrackTitle ?? "Album",
            ImageUrl = ViewModel.AlbumArt
        };

        if (albumId.StartsWith("spotify:show:", StringComparison.Ordinal))
        {
            NavigationHelpers.OpenShowPage(albumId, param.Title);
            return;
        }

        NavigationHelpers.OpenAlbum(param, param.Title);
    }

    private void NavigateToCurrentSubtitleTarget()
    {
        if (TryOpenLocalContentTarget())
            return;

        var artistUri = ViewModel.CurrentArtistId;
        var artistName = ViewModel.ArtistName;

        if (ViewModel.CurrentArtists is { Count: > 0 } artists)
        {
            foreach (var artist in artists)
            {
                if (string.IsNullOrWhiteSpace(artist.Uri)) continue;
                artistUri = artist.Uri;
                artistName = string.IsNullOrWhiteSpace(artist.Name) ? artistName : artist.Name;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(artistUri)) return;

        var param = new ContentNavigationParameter
        {
            Uri = artistUri,
            Title = string.IsNullOrWhiteSpace(artistName) ? "Artist" : artistName
        };
        NavigationHelpers.OpenArtist(param, param.Title);
    }

    private bool TryOpenLocalContentTarget()
    {
        if (_playbackStateService is null) return false;

        switch (_playbackStateService.CurrentLocalContentKind)
        {
            case Wavee.Local.Classification.LocalContentKind.TvEpisode
                when !string.IsNullOrWhiteSpace(_playbackStateService.CurrentLocalSeriesId):
                NavigationHelpers.OpenLocalShowDetail(
                    _playbackStateService.CurrentLocalSeriesId!,
                    _playbackStateService.CurrentLocalSeriesName ?? ViewModel.ArtistName);
                return true;

            case Wavee.Local.Classification.LocalContentKind.Movie
                when !string.IsNullOrWhiteSpace(_playbackStateService.CurrentTrackId):
                NavigationHelpers.OpenLocalMovieDetail(
                    _playbackStateService.CurrentTrackId!,
                    ViewModel.TrackTitle);
                return true;

            default:
                return false;
        }
    }

    // ── Heart wiring (mirrors PlayerBar.xaml.cs) ──────────────────────────

    private void UpdateHeartState()
    {
        var version = ++_heartStateVersion;
        var uri = PlaybackSaveTargetResolver.GetTrackUri(_playbackStateService);
        if (!string.IsNullOrEmpty(uri))
        {
            HeartButtonCtrl.IsLiked = _likeService?.IsSaved(SavedItemType.Track, uri) == true;
            return;
        }

        HeartButtonCtrl.IsLiked = false;
        _ = UpdateHeartStateAsync(version);
    }

    private async Task UpdateHeartStateAsync(int version)
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);

        if (version != _heartStateVersion) return;

        HeartButtonCtrl.IsLiked = !string.IsNullOrEmpty(uri)
            && _likeService?.IsSaved(SavedItemType.Track, uri) == true;
    }

    private void OnHeartClicked() => _ = OnHeartClickedAsync();

    private async Task OnHeartClickedAsync()
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(uri) || _likeService == null) return;

        var isLiked = _likeService.IsSaved(SavedItemType.Track, uri);
        _likeService.ToggleSave(SavedItemType.Track, uri, isLiked);
        HeartButtonCtrl.IsLiked = !isLiked;
    }

    // Track menu (audio / video / subtitle) moved into VideoTransportBar
    // alongside the rest of the YouTube-style transport.

    // ── Subtitle drag-drop ────────────────────────────────────────────────

    private static readonly System.Collections.Generic.HashSet<string> SubtitleExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".vtt", ".ass", ".ssa", ".sub", ".idx"
        };

    private async void StageGrid_DragOver(object sender, DragEventArgs e)
    {
        var hasSubtitle = await HasSubtitleStorageItemAsync(e.DataView);
        if (hasSubtitle)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add subtitle";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            SubtitleDropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SubtitleDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void StageGrid_DragLeave(object sender, DragEventArgs e)
        => SubtitleDropOverlay.Visibility = Visibility.Collapsed;

    private async void StageGrid_Drop(object sender, DragEventArgs e)
    {
        SubtitleDropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var local = Ioc.Default.GetService<LocalMediaPlayer>();
        if (local is null) return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is not StorageFile file) continue;
            if (!SubtitleExtensions.Contains(System.IO.Path.GetExtension(file.Name))) continue;

            // Reuse the indexer's heuristic so "Movie.eng.forced.srt" parses
            // to (lang=en, forced=true) — same shape the scanner would have
            // produced if the user had dropped the file into the watched
            // folder before scanning.
            var parsed = Wavee.Local.Subtitles.LocalSubtitleDiscoverer.ParseFromPath(file.Path);
            var stem = System.IO.Path.GetFileNameWithoutExtension(file.Name);

            await local.AddExternalSubtitleAsync(
                file.Path,
                parsed.Language,
                label: stem,
                forced: parsed.Forced,
                sdh: parsed.Sdh);
        }
    }

    private static async Task<bool> HasSubtitleStorageItemAsync(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.StorageItems)) return false;
        try
        {
            var items = await view.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is not StorageFile file) continue;
                if (SubtitleExtensions.Contains(System.IO.Path.GetExtension(file.Name)))
                    return true;
            }
        }
        catch
        {
            // GetStorageItemsAsync can throw if the drag was cancelled —
            // treat as "no subtitle" rather than tripping the user.
        }
        return false;
    }
}
