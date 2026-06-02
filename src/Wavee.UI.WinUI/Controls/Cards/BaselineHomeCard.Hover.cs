using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Hover state machine for <see cref="BaselineHomeCard"/>: pointer enter / exit /
/// canceled, scale + chrome reveal animations, the version-counter guard that
/// keeps superseded hover activations from racing the real one, root-level
/// pointer routing so window-edge exits clean up correctly, and the deferred
/// canvas teardown that gives exit animations a window to render before the
/// MediaFoundation unwind.
///
/// <para>Kept inline rather than extracted to a behavior because the hover
/// state cross-cuts most other partials — it reads the canvas / preview-audio
/// flags, calls into <c>StopHoverMedia</c> which in turn drives canvas,
/// preview audio, visualiser, and preview-navigation resets, and owns the
/// <c>s_activeCard</c> single-active-card transfer logic.</para>
/// </summary>
public sealed partial class BaselineHomeCard
{
    private const int MaxDeferredHoverStateRefreshAttempts = 4;

    private bool _isPointerOver;
    private bool _isHoverStateRefreshQueued;
    private int _deferredHoverStateRefreshAttempts;
    private int _hoverEnterVersion;
    private int _hoverStopVersion;
    private bool _hoverEnterGuardActive;
    private Windows.Foundation.Point _lastPointerWindowPosition;
    private bool _hasLastPointerWindowPosition;
    private UIElement? _rootPointerElement;

    // ── Card-level pointer events ────────────────────────────────────────────

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Wavee.UI.WinUI.DragDrop.PointerInput.IsTouch(e)) return; // touch has no hover (issue #4)
        UpdateLastPointerWindowPosition(e);
        _hoverStopVersion++;
        _isPointerOver = true;
        _deferredHoverStateRefreshAttempts = 0;
        var hoverEnterVersion = ++_hoverEnterVersion;
        TraceCard($"PointerEntered hoverEnterVersion={hoverEnterVersion}");

        if (s_activeCard != null && !ReferenceEquals(s_activeCard, this))
            s_activeCard.StopHoverMedia();

        s_activeCard = this;
        _hoverEnterGuardActive = true;
        QueueHoverEnterActivation(hoverEnterVersion);
    }

    private void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);
        if (ShouldSuppressHoverExit(e))
        {
            TraceCard("PointerExited suppressed");
            _hoverEnterGuardActive = false;
            _isPointerOver = true;
            return;
        }
        TraceCard("PointerExited -> StopHoverMedia");
        StopHoverMedia();
    }

    private void Card_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);
        if (ShouldSuppressHoverExit(e))
        {
            TraceCard("PointerCanceled suppressed");
            _hoverEnterGuardActive = false;
            _isPointerOver = true;
            return;
        }
        TraceCard("PointerCanceled -> StopHoverMedia");
        StopHoverMedia();
    }

    // ── Root-level pointer routing ───────────────────────────────────────────

    private void AttachRootPointerHandlers()
    {
        if (ReferenceEquals(_rootPointerElement, XamlRoot?.Content))
            return;

        DetachRootPointerHandlers();

        if (XamlRoot?.Content is not UIElement root)
            return;

        root.PointerMoved += Root_PointerMoved;
        root.PointerExited += Root_PointerExited;
        root.PointerCanceled += Root_PointerCanceled;
        _rootPointerElement = root;
    }

    private void DetachRootPointerHandlers()
    {
        if (_rootPointerElement == null)
            return;

        _rootPointerElement.PointerMoved -= Root_PointerMoved;
        _rootPointerElement.PointerExited -= Root_PointerExited;
        _rootPointerElement.PointerCanceled -= Root_PointerCanceled;
        _rootPointerElement = null;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);

        if (_isPointerOver && _hasLastPointerWindowPosition && !IsPointWithinCardBounds(_lastPointerWindowPosition))
        {
            TraceCard("Root pointer moved outside card -> StopHoverMedia");
            StopHoverMedia();
        }
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hasLastPointerWindowPosition = false;
        if (_isPointerOver)
        {
            TraceCard("Root pointer exited window -> StopHoverMedia");
            StopHoverMedia();
        }
    }

    private void Root_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _hasLastPointerWindowPosition = false;
        if (_isPointerOver)
        {
            TraceCard("Root pointer canceled -> StopHoverMedia");
            StopHoverMedia();
        }
    }

    // ── Hover activation (versioned) ─────────────────────────────────────────

    private void QueueHoverEnterActivation(int hoverEnterVersion)
    {
        TraceCard($"QueueHoverEnterActivation hoverEnterVersion={hoverEnterVersion}");
        if (!DispatcherQueue.TryEnqueue(() =>
            ActivateHoverStateIfCurrent(hoverEnterVersion)))
        {
            TraceCard($"QueueHoverEnterActivation failed to enqueue hoverEnterVersion={hoverEnterVersion}");
            _hoverEnterGuardActive = false;
        }
    }

    private void ActivateHoverStateIfCurrent(int hoverEnterVersion)
    {
        if (!_isPointerOver || hoverEnterVersion != _hoverEnterVersion)
        {
            TraceCard($"ActivateHoverStateIfCurrent ignored hoverEnterVersion={hoverEnterVersion}");
            _hoverEnterGuardActive = false;
            return;
        }

        if (!IsLoaded)
        {
            TraceCard($"ActivateHoverStateIfCurrent blocked by !IsLoaded hoverEnterVersion={hoverEnterVersion}");
            _hoverEnterGuardActive = false;
            return;
        }

        TraceCard($"ActivateHoverStateIfCurrent applying hoverEnterVersion={hoverEnterVersion}");
        EnsureHoverChromeRealized();
        ApplyHoverState();

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
        if (hasPreviewAudio && CanStartHoverPlayback())
        {
            TraceCard("ActivateHoverStateIfCurrent scheduling preview audio");
            _ = SchedulePreviewAudioAsync();
        }

        if (hasPreviewAudio && TrackPlayButton == null)
            QueueDeferredHoverStateRefresh();

        _hoverEnterGuardActive = false;
    }

    private void ApplyHoverState()
    {
        if (HoverChrome != null)
            HoverChrome.Visibility = Visibility.Visible;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());

        if (TrackPlayButton != null)
            TrackPlayButton.Visibility = hasPreviewAudio ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewButtonVisualState();

        UpdatePlayingState();
        UpdatePreviewVisualState(hasPreviewAudio);

        var hasMultiplePreviewTracks = (Item?.PreviewTracks.Count ?? 0) > 1;
        if (hasMultiplePreviewTracks)
            EnsurePreviewNavigationButtonsRealized();
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;

        if (HoverChrome != null)
        {
            // Set opacity directly first to guarantee visibility,
            // then run the animation for smoothness on subsequent hovers
            HoverChrome.Opacity = 1;
            AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(140))
                .Start(HoverChrome);
        }

        Canvas.SetZIndex(this, 10);
        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(CardRoot.ActualWidth / 2),
            (float)(CardRoot.ActualHeight / 2),
            0);

        AnimationBuilder.Create()
            .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.025f), duration: TimeSpan.FromMilliseconds(180))
            .Start(CardRoot);

        _ = StartCanvasPreviewAsync();
    }

    // ── Hover-exit suppression + bounds helpers ──────────────────────────────

    private bool ShouldSuppressHoverExit(PointerRoutedEventArgs e)
    {
        if (_hoverEnterGuardActive)
            return true;

        try
        {
            var windowPoint = e.GetCurrentPoint(null).Position;
            return IsPointWithinCardBounds(windowPoint) ||
                   (_hasLastPointerWindowPosition && IsPointWithinCardBounds(_lastPointerWindowPosition));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[BaselineHomeCard] Hover exit suppression check failed: {ex.Message}");
            return false;
        }
    }

    private bool CanStartHoverPlayback()
    {
        if (!_isPointerOver || !IsLoaded)
            return false;

        return !_hasLastPointerWindowPosition || IsPointWithinCardBounds(_lastPointerWindowPosition);
    }

    private void UpdateLastPointerWindowPosition(PointerRoutedEventArgs e)
    {
        try
        {
            _lastPointerWindowPosition = e.GetCurrentPoint(null).Position;
            _hasLastPointerWindowPosition = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[BaselineHomeCard] Pointer position capture failed: {ex.Message}");
        }
    }

    private bool IsPointWithinCardBounds(Windows.Foundation.Point windowPoint)
    {
        try
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || XamlRoot?.Content is not UIElement root)
                return false;

            var bounds = TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
            return windowPoint.X >= bounds.Left &&
                   windowPoint.X <= bounds.Right &&
                   windowPoint.Y >= bounds.Top &&
                   windowPoint.Y <= bounds.Bottom;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[BaselineHomeCard] Card bounds check failed: {ex.Message}");
            return false;
        }
    }

    // ── Stop / collapse ──────────────────────────────────────────────────────

    private void StopHoverMedia(bool deferCanvasTeardown = true)
    {
        TraceCard($"StopHoverMedia deferCanvasTeardown={deferCanvasTeardown}");
        _hoverEnterGuardActive = false;
        _hoverEnterVersion++;
        var stopVersion = ++_hoverStopVersion;
        CancelPreviewTransition(resetMotionHosts: true);
        _isPointerOver = false;
        _isHoverStateRefreshQueued = false;
        _deferredHoverStateRefreshAttempts = 0;
        StopPreviewVisualization();
        StopPreviewAudio();

        if (TrackPlayButton != null)
            TrackPlayButton.Visibility = Visibility.Collapsed;
        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = Visibility.Collapsed;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(false);
            PreviewVisualizer.Visibility = Visibility.Collapsed;
        }

        UpdatePlayingState();

        if (IsLoaded)
        {
            if (HoverChrome != null)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120))
                    .Start(HoverChrome);
            }

            var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(CardRoot.ActualWidth / 2),
                (float)(CardRoot.ActualHeight / 2),
                0);

            AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.025f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(160))
                .Start(CardRoot);

            _ = CollapseHoverChromeAsync();
            Canvas.SetZIndex(this, 0);
        }

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;

        if (deferCanvasTeardown)
        {
            _ = DeferredStopCanvasPreviewAsync(stopVersion);
        }
        else
        {
            StopCanvasPreview();
        }
    }

    // The MediaPlayerElement teardown (Source = null) unwinds MediaFoundation on the UI
    // thread and can visibly stall rapid hover-exit sweeps. Give the exit animations a
    // window to render, then drop to Low priority so the teardown only runs when the UI
    // thread is otherwise idle. If the user re-enters the card or a newer stop supersedes
    // us, the version check skips the teardown entirely.
    private async Task DeferredStopCanvasPreviewAsync(int stopVersion)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(180));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (stopVersion != _hoverStopVersion || _isPointerOver)
                return;

            StopCanvasPreview();
        });
    }

    private void QueueDeferredHoverStateRefresh()
    {
        if (!_isPointerOver ||
            _isHoverStateRefreshQueued ||
            _deferredHoverStateRefreshAttempts >= MaxDeferredHoverStateRefreshAttempts)
            return;

        _isHoverStateRefreshQueued = true;
        _deferredHoverStateRefreshAttempts++;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _isHoverStateRefreshQueued = false;
            if (!_isPointerOver || !IsLoaded)
                return;

            ApplyHoverState();

            var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
            if (hasPreviewAudio && TrackPlayButton == null)
                QueueDeferredHoverStateRefresh();
        }))
        {
            _isHoverStateRefreshQueued = false;
        }
    }

    private async Task CollapseHoverChromeAsync()
    {
        await Task.Delay(150);
        if (!_isPointerOver && IsLoaded && HoverChrome != null)
            HoverChrome.Visibility = Visibility.Collapsed;
    }
}
