using System;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Controls.MiniVideoPlayer;

/// <summary>
/// Bottom-right floating mini-player. Becomes visible whenever a video
/// provider is active and the user is not currently on
/// <see cref="Wavee.UI.WinUI.Views.VideoPlayerPage"/>. Click anywhere on the
/// video area to re-open the page; the close button stops playback.
/// </summary>
/// <remarks>
/// Mounted at shell level (in <c>ShellPage.xaml</c>'s Row 2) so navigation
/// inside the content <c>Frame</c> doesn't tear it down. Its
/// <see cref="MediaPlayerElement"/> is acquired through
/// <see cref="IActiveVideoSurfaceService"/> rather than a concrete engine,
/// so it works equally well for local files and (future) Spotify videos.
/// </remarks>
public sealed partial class MiniVideoPlayer : UserControl, IMediaSurfaceConsumer
{
    private readonly IActiveVideoSurfaceService _surface;
    public MiniVideoPlayerViewModel ViewModel { get; }

    private MediaPlayerElement? _element;
    private FrameworkElement? _elementSurface;

    // Auto-hide of OverlayButtons (expand / close / prev-play-next). Fades
    // out after IdleHideMs of no pointer activity over the control. The
    // bottom position bar stays visible regardless — same affordance YouTube
    // keeps when their hover controls fade.
    private DispatcherQueueTimer? _hideTimer;
    private const int IdleHideMs = 2500;
    private const int FadeDurationMs = 180;
    private bool _overlayVisible = true;

    // Drag state. Translation on the UserControl shifts the visual position
    // without affecting layout. Pointer coords are read relative to the
    // parent container so the reference frame stays stable while the
    // control itself moves.
    private bool _isDragging;
    private Point _dragStartPointerPos;
    private Vector3 _dragStartTranslation;

    // Resize state. The control is anchored bottom-right at the shell, so
    // growing Width/Height extends toward the top-left — exactly what the
    // bottom-right grip drag should feel like (anchor stays put visually).
    private bool _isResizing;
    private Point _resizeStartPointerPos;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private Vector3 _resizeStartTranslation;

    // Cached system cursor instances. ProtectedCursor is settable on
    // any Control (UserControl is a Control), so we toggle this control's
    // cursor on PointerEntered of each interactive zone.
    private static readonly InputCursor _cursorSizeNwSe =
        InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private static readonly InputCursor _cursorSizeNeSw =
        InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    private static readonly InputCursor _cursorSizeAll =
        InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    public MiniVideoPlayer()
    {
        InitializeComponent();
        _surface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        ViewModel = Ioc.Default.GetRequiredService<MiniVideoPlayerViewModel>();
        DataContext = ViewModel;

        // Visibility tracks ViewModel.IsVisible (composite of "video active"
        // + "not on the video page"). When we become visible we acquire the
        // surface; when we hide we release it.
        _surface.ActiveSurfaceChanged += OnActiveVideoSurfaceStateChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Visibility = ViewModel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // Idempotent: created on first Loaded, kept alive across show/hide.
    private void EnsureHideTimer()
    {
        if (_hideTimer is not null) return;
        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq is null) return;
        _hideTimer = dq.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(IdleHideMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => FadeOverlay(visible: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureHideTimer();
        if (ViewModel.IsVisible)
        {
            _surface.AcquireSurface(this);
            // Auto-fade overlay buttons even when the cursor never enters
            // the control — first-paint should converge to the same idle
            // state as a fade-out after hover.
            RestartHideTimer();
        }
        UpdateVideoStatusOverlay();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _surface.ReleaseSurface(this);
        _surface.ActiveSurfaceChanged -= OnActiveVideoSurfaceStateChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnActiveVideoSurfaceStateChanged(object? sender, MediaPlayer? surface)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnActiveVideoSurfaceStateChanged(sender, surface));
            return;
        }

        UpdateVideoStatusOverlay();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MiniVideoPlayerViewModel.IsVisible)) return;
        if (ViewModel.IsVisible)
        {
            Visibility = Visibility.Visible;
            _surface.AcquireSurface(this);
            // Reset overlay to visible-then-fade on every show so a returning
            // user sees the controls instead of a frame-only mini.
            FadeOverlay(visible: true);
            RestartHideTimer();
        }
        else
        {
            _surface.ReleaseSurface(this);
            Visibility = Visibility.Collapsed;
        }
        UpdateVideoStatusOverlay();
    }

    private void ExpandClickArea_Click(object sender, RoutedEventArgs e)
        => ViewModel.ExpandCommand.Execute(null);

    // ── IMediaSurfaceConsumer ─────────────────────────────────────────────

    public void AttachSurface(MediaPlayer player)
    {
        DetachElementSurface();
        if (_element is null)
        {
            _element = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false, // clicks bubble to ExpandClickArea
            };
            // Row 0 of MiniVideoHost (the Button overlay sits at higher Z).
            MiniVideoHost.Children.Insert(0, _element);
        }
        _element.SetMediaPlayer(player);
        UpdateVideoStatusOverlay();
    }

    public void AttachElementSurface(FrameworkElement element)
    {
        DetachMediaPlayerSurface();
        if (_elementSurface is not null && ReferenceEquals(_elementSurface, element))
            return;

        _elementSurface = element;
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.IsHitTestVisible = false;
        MiniVideoHost.Children.Insert(0, element);
        UpdateVideoStatusOverlay();
    }

    public void DetachSurface()
    {
        DetachMediaPlayerSurface();
        DetachElementSurface();
        UpdateVideoStatusOverlay();
    }

    private void UpdateVideoStatusOverlay()
    {
        if (MiniVideoStatusOverlay is null) return;
        var hasAttachedVideo = _element is not null || _elementSurface is not null;
        var showLoading = ViewModel.IsVisible
            && hasAttachedVideo
            && _surface.HasActiveSurface
            && !_surface.HasActiveFirstFrame;
        var showBuffering = ViewModel.IsVisible
            && hasAttachedVideo
            && _surface.HasActiveSurface
            && _surface.HasActiveFirstFrame
            && _surface.IsActiveSurfaceBuffering;

        MiniVideoStatusText.Text = showBuffering ? "Buffering" : "Loading";
        MiniVideoStatusOverlay.Visibility = showLoading || showBuffering
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DetachMediaPlayerSurface()
    {
        if (_element is null) return;
        _element.SetMediaPlayer(null);
        MiniVideoHost.Children.Remove(_element);
        _element = null;
    }

    private void DetachElementSurface()
    {
        if (_elementSurface is null) return;
        MiniVideoHost.Children.Remove(_elementSurface);
        _elementSurface.IsHitTestVisible = true;
        _elementSurface = null;
    }

    // ── Auto-hide overlay buttons ─────────────────────────────────────────

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        FadeOverlay(visible: true);
        RestartHideTimer();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        FadeOverlay(visible: true);
        RestartHideTimer();
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Cursor left the control entirely — hide promptly.
        _hideTimer?.Stop();
        FadeOverlay(visible: false);
    }

    private void RestartHideTimer()
    {
        // Don't auto-hide while the user is actively manipulating the control.
        if (_isDragging || _isResizing) return;
        if (_hideTimer is null) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // Animated Opacity transition for the OverlayButtons group only. The
    // chrome bar (title + drag-handle + resize-grip) stays visible because
    // the title is informative and the drag/resize affordances need to be
    // discoverable. Position bar lives outside this group too — always on,
    // matching YouTube's mini-player.
    private void FadeOverlay(bool visible)
    {
        if (_overlayVisible == visible && OverlayButtons.Opacity == (visible ? 1 : 0))
            return;
        _overlayVisible = visible;

        var sb = new Storyboard();
        AddOpacityAnimation(sb, OverlayButtons, visible ? 1.0 : 0.0);
        sb.Begin();

        // Block hit-testing on the buttons while invisible so a stray click
        // on a faded close-X doesn't kill playback.
        OverlayButtons.IsHitTestVisible = visible;
    }

    private static void AddOpacityAnimation(Storyboard sb, UIElement target, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(FadeDurationMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
    }

    // ── Drag (chrome bar = drag handle) ───────────────────────────────────

    private void ChromeBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Don't start a drag if the user grabbed the resize grip — its own
        // PointerPressed will run independently and set _isResizing.
        if (_isResizing) return;
        var parent = Parent as UIElement;
        if (parent is null) return;
        _isDragging = true;
        _dragStartPointerPos = e.GetCurrentPoint(parent).Position;
        _dragStartTranslation = Translation;
        _hideTimer?.Stop();
        ChromeBar.CapturePointer(e.Pointer);
    }

    private void ChromeBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var parent = Parent as UIElement;
        if (parent is null) return;
        var pos = e.GetCurrentPoint(parent).Position;
        var dx = (float)(pos.X - _dragStartPointerPos.X);
        var dy = (float)(pos.Y - _dragStartPointerPos.Y);
        Translation = _dragStartTranslation + new Vector3(dx, dy, 0);
    }

    private void ChromeBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndDrag(e.Pointer);

    private void ChromeBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndDrag(e.Pointer);

    private void EndDrag(Pointer pointer)
    {
        if (!_isDragging) return;
        _isDragging = false;
        try { ChromeBar.ReleasePointerCapture(pointer); } catch { }
        RestartHideTimer();
    }

    // ── Resize (BR grip on the chrome bar) ────────────────────────────────

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var parent = Parent as UIElement;
        if (parent is null) return;
        _isResizing = true;
        _resizeStartPointerPos = e.GetCurrentPoint(parent).Position;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizeStartTranslation = Translation;
        _hideTimer?.Stop();
        ResizeGrip.CapturePointer(e.Pointer);
        e.Handled = true; // prevent ChromeBar drag from firing
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing) return;
        var parent = Parent as UIElement;
        if (parent is null) return;
        var pos = e.GetCurrentPoint(parent).Position;
        var dx = pos.X - _resizeStartPointerPos.X;
        var dy = pos.Y - _resizeStartPointerPos.Y;

        // The control is anchored bottom-right at the shell, so growing
        // Width/Height alone would push the TL leftward and pin the BR.
        // To make the BR follow the cursor (i.e. grow toward the cursor),
        // we translate the control by the same delta we add to the size.
        // Net visual effect: TL stays put, BR moves by (dx, dy) — exactly
        // what dragging a BR resize grip should feel like.
        var newW = Math.Max(MinWidth, _resizeStartWidth + dx);
        var newH = Math.Max(MinHeight, _resizeStartHeight + dy);
        var actualDx = (float)(newW - _resizeStartWidth);
        var actualDy = (float)(newH - _resizeStartHeight);

        Width = newW;
        Height = newH;
        Translation = _resizeStartTranslation + new Vector3(actualDx, actualDy, 0);
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndResize(e.Pointer);

    private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndResize(e.Pointer);

    private void EndResize(Pointer pointer)
    {
        if (!_isResizing) return;
        _isResizing = false;
        try { ResizeGrip.ReleasePointerCapture(pointer); } catch { }
        RestartHideTimer();
    }

    // ── Top-corner resize grips ──────────────────────────────────────────
    //
    // BR-anchored layout: with HorizontalAlignment=Right and
    // VerticalAlignment=Bottom set on the UserControl in ShellPage.xaml,
    // growing Width pushes TL leftward (BR pinned), growing Height pushes
    // TL upward (BR pinned). The math for each corner solves "the dragged
    // corner follows the cursor; the diagonally opposite corner stays
    // planted" — see plan Piece 2c for the derivation.

    private void ResizeGripTL_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var parent = Parent as UIElement;
        if (parent is null) return;
        _isResizing = true;
        _resizeStartPointerPos = e.GetCurrentPoint(parent).Position;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizeStartTranslation = Translation;
        _hideTimer?.Stop();
        ResizeGripTL.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeGripTL_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing) return;
        var parent = Parent as UIElement;
        if (parent is null) return;
        var pos = e.GetCurrentPoint(parent).Position;
        var dx = pos.X - _resizeStartPointerPos.X;
        var dy = pos.Y - _resizeStartPointerPos.Y;
        // TL grip: cursor moved (dx, dy) → control TL moves the same way → BR
        // is anchored by the layout slot, so it stays put automatically.
        // Width and Height shrink by (dx, dy); no translation change.
        Width = Math.Max(MinWidth, _resizeStartWidth - dx);
        Height = Math.Max(MinHeight, _resizeStartHeight - dy);
    }

    private void ResizeGripTL_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndResizeTL(e.Pointer);

    private void ResizeGripTL_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndResizeTL(e.Pointer);

    private void EndResizeTL(Pointer pointer)
    {
        if (!_isResizing) return;
        _isResizing = false;
        try { ResizeGripTL.ReleasePointerCapture(pointer); } catch { }
        RestartHideTimer();
    }

    private void ResizeGripTR_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var parent = Parent as UIElement;
        if (parent is null) return;
        _isResizing = true;
        _resizeStartPointerPos = e.GetCurrentPoint(parent).Position;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizeStartTranslation = Translation;
        _hideTimer?.Stop();
        ResizeGripTR.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeGripTR_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing) return;
        var parent = Parent as UIElement;
        if (parent is null) return;
        var pos = e.GetCurrentPoint(parent).Position;
        var dx = pos.X - _resizeStartPointerPos.X;
        var dy = pos.Y - _resizeStartPointerPos.Y;
        // TR grip: grow Width on +dx (drag right), shrink Height on +dy
        // (drag up = dy negative). Translation.X moves with the actual width
        // delta so the BL stays planted; Translation.Y stays so the BR's Y
        // also stays planted.
        var newW = Math.Max(MinWidth, _resizeStartWidth + dx);
        var newH = Math.Max(MinHeight, _resizeStartHeight - dy);
        var actualDx = (float)(newW - _resizeStartWidth);
        Width = newW;
        Height = newH;
        Translation = _resizeStartTranslation + new Vector3(actualDx, 0, 0);
    }

    private void ResizeGripTR_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndResizeTR(e.Pointer);

    private void ResizeGripTR_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndResizeTR(e.Pointer);

    private void EndResizeTR(Pointer pointer)
    {
        if (!_isResizing) return;
        _isResizing = false;
        try { ResizeGripTR.ReleasePointerCapture(pointer); } catch { }
        RestartHideTimer();
    }

    // ── Cursor changes ───────────────────────────────────────────────────

    private void ResizeGripTL_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = _cursorSizeNwSe;

    private void ResizeGripTR_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = _cursorSizeNeSw;

    private void ResizeGripBR_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = _cursorSizeNwSe;

    private void ResizeGrip_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Don't reset mid-drag: the cursor should stay as the resize shape
        // for the entire duration of the drag, even if the pointer briefly
        // leaves the grip Border.
        if (_isResizing) return;
        // BR grip lives INSIDE the chrome bar — when pointer leaves the grip
        // it typically lands back on the chrome, so restore the move cursor
        // to keep the affordance smooth. TL/TR grips live inside the video
        // host, where the natural exit destination has no cursor, so reset
        // to null. Chrome's own PointerExited will null it out if pointer
        // leaves the control entirely.
        if (sender == ResizeGrip)
            ProtectedCursor = _cursorSizeAll;
        else
            ProtectedCursor = null;
    }

    private void ChromeBar_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = _cursorSizeAll;

    private void ChromeBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging) return;
        ProtectedCursor = null;
    }
}
