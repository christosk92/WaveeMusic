using System;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;

    private bool _draggingResizer;
    private double _preManipulationWidth;

    // Lyrics state
    private readonly LyricsViewModel _lyricsVm;
    private Compositor? _compositor;
    private SpriteVisual? _fadeMaskSprite;
    private CompositionVisualSurface? _scrollSurface;
    private int _prevActiveIndex = -1;

    public RightPanelView()
    {
        _lyricsVm = Ioc.Default.GetRequiredService<LyricsViewModel>();

        InitializeComponent();
        Visibility = Visibility.Collapsed;
        Width = PanelWidth;

        Loaded += RightPanelView_Loaded;
        Unloaded += RightPanelView_Unloaded;
    }

    private void RightPanelView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateContentVisibility();

        _lyricsVm.ActiveLineChanged += OnActiveLineChanged;

        var visual = ElementCompositionPreview.GetElementVisual(this);
        _compositor = visual.Compositor;

        SetupScrollViewerFadeMask();
    }

    private void RightPanelView_Unloaded(object sender, RoutedEventArgs e)
    {
        _lyricsVm.ActiveLineChanged -= OnActiveLineChanged;
        _lyricsVm.IsVisible = false;

        if (LyricsScrollViewer != null)
            LyricsScrollViewer.SizeChanged -= OnScrollViewerSizeChanged;

        // Dispose composition resources
        if (_fadeMaskSprite != null)
        {
            ElementCompositionPreview.SetElementChildVisual(LyricsFadeMaskHost, null);
            _fadeMaskSprite.Brush?.Dispose();
            _fadeMaskSprite.Dispose();
            _fadeMaskSprite = null;
        }
        if (_scrollSurface != null)
        {
            // Restore original ScrollViewer visual
            if (LyricsScrollViewer != null)
            {
                var sv = ElementCompositionPreview.GetElementVisual(LyricsScrollViewer);
                sv.Opacity = 1f;
            }
            _scrollSurface.Dispose();
            _scrollSurface = null;
        }

        _prevActiveIndex = -1;
        _compositor = null;
    }

    private void UpdateContentVisibility()
    {
        if (QueueContent == null) return;

        QueueContent.Visibility = SelectedMode == RightPanelMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsContent.Visibility = SelectedMode == RightPanelMode.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        FriendsContent.Visibility = SelectedMode == RightPanelMode.FriendsActivity ? Visibility.Visible : Visibility.Collapsed;

        QueueTab.IsChecked = SelectedMode == RightPanelMode.Queue;
        LyricsTab.IsChecked = SelectedMode == RightPanelMode.Lyrics;
        FriendsTab.IsChecked = SelectedMode == RightPanelMode.FriendsActivity;

        _lyricsVm.IsVisible = SelectedMode == RightPanelMode.Lyrics && IsOpen;
    }

    // ── Lyrics: alpha fade mask on LyricsContent grid via CompositionVisualSurface ──
    //
    // Technique: capture LyricsContent's visual into a CompositionVisualSurface,
    // render it through a MaskBrush (source × alpha gradient) onto a SpriteVisual
    // placed on a sibling container. The original LyricsContent is hidden.
    // This avoids the circular parent-child issue.

    private void SetupScrollViewerFadeMask()
    {
        if (_compositor == null || LyricsScrollViewer == null || LyricsFadeMaskHost == null) return;

        // Get the ScrollViewer's visual
        var scrollVisual = ElementCompositionPreview.GetElementVisual(LyricsScrollViewer);

        // Capture into a CompositionVisualSurface
        _scrollSurface = _compositor.CreateVisualSurface();
        _scrollSurface.SourceVisual = scrollVisual;
        _scrollSurface.SourceSize = new Vector2(
            Math.Max(1, (float)LyricsScrollViewer.ActualWidth),
            Math.Max(1, (float)LyricsScrollViewer.ActualHeight));

        var surfaceBrush = _compositor.CreateSurfaceBrush(_scrollSurface);
        surfaceBrush.Stretch = CompositionStretch.None;

        // Alpha gradient mask (HeroHeader pattern: white alpha channel)
        var gradientMask = _compositor.CreateLinearGradientBrush();
        gradientMask.StartPoint = new Vector2(0.5f, 0f);
        gradientMask.EndPoint = new Vector2(0.5f, 1f);

        var transparent = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        var opaque = Windows.UI.Color.FromArgb(255, 255, 255, 255);

        gradientMask.ColorStops.Add(_compositor.CreateColorGradientStop(0.00f, transparent));
        gradientMask.ColorStops.Add(_compositor.CreateColorGradientStop(0.06f, opaque));
        gradientMask.ColorStops.Add(_compositor.CreateColorGradientStop(0.88f, opaque));
        gradientMask.ColorStops.Add(_compositor.CreateColorGradientStop(1.00f, transparent));

        // Combine: captured content × alpha mask
        var maskBrush = _compositor.CreateMaskBrush();
        maskBrush.Source = surfaceBrush;
        maskBrush.Mask = gradientMask;

        // Sprite visual renders the masked content
        _fadeMaskSprite = _compositor.CreateSpriteVisual();
        _fadeMaskSprite.Brush = maskBrush;
        _fadeMaskSprite.RelativeSizeAdjustment = Vector2.One;

        // Place the masked sprite on the sibling host (LyricsFadeMaskHost overlays the ScrollViewer)
        ElementCompositionPreview.SetElementChildVisual(LyricsFadeMaskHost, _fadeMaskSprite);

        // Hide the original ScrollViewer visual — the sprite replaces it
        scrollVisual.Opacity = 0f;

        LyricsScrollViewer.SizeChanged += OnScrollViewerSizeChanged;
    }

    private void OnScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_scrollSurface != null)
        {
            _scrollSurface.SourceSize = new Vector2(
                Math.Max(1, (float)e.NewSize.Width),
                Math.Max(1, (float)e.NewSize.Height));
        }
    }

    // ── Lyrics: active line animation + progressive dimming ──

    private void OnActiveLineChanged(int newIndex, int prevIndex)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_compositor == null) return;

            var lineCount = _lyricsVm.Lines.Count;

            for (var i = 0; i < lineCount; i++)
            {
                var element = LyricsRepeater.TryGetElement(i);
                if (element is not FrameworkElement fe) continue;

                var visual = ElementCompositionPreview.GetElementVisual(fe);
                var distance = Math.Abs(i - newIndex);

                if (i == newIndex)
                {
                    // Active line: full opacity, slight scale
                    visual.CenterPoint = new Vector3(
                        (float)fe.ActualWidth / 2,
                        (float)fe.ActualHeight / 2, 0);

                    AnimationBuilder.Create()
                        .Scale(to: new Vector3(1.02f), duration: TimeSpan.FromMilliseconds(300))
                        .Opacity(to: 1.0, duration: TimeSpan.FromMilliseconds(300))
                        .Start(fe);
                }
                else
                {
                    // Progressive dimming: closer lines are more visible
                    var targetOpacity = distance switch
                    {
                        1 => 0.45,
                        2 => 0.30,
                        _ => 0.18
                    };

                    visual.CenterPoint = new Vector3(
                        (float)fe.ActualWidth / 2,
                        (float)fe.ActualHeight / 2, 0);

                    AnimationBuilder.Create()
                        .Scale(to: Vector3.One, duration: TimeSpan.FromMilliseconds(250))
                        .Opacity(to: targetOpacity, duration: TimeSpan.FromMilliseconds(300))
                        .Start(fe);
                }
            }

            // Auto-scroll active line to ~25% from top
            var activeElement = LyricsRepeater.TryGetElement(newIndex);
            if (activeElement is FrameworkElement activeFe)
            {
                activeFe.StartBringIntoView(new BringIntoViewOptions
                {
                    VerticalAlignmentRatio = 0.25,
                    AnimationDesired = true
                });
            }

            _prevActiveIndex = newIndex;
        });
    }

    // ── Tab header clicks ──

    private void QueueTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Queue;

    private void LyricsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Lyrics;

    private void FriendsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.FriendsActivity;

    // ── Resize gripper ──

    private void Resizer_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _draggingResizer = true;
        _preManipulationWidth = PanelWidth;
        VisualStateManager.GoToState(this, "ResizerPressed", true);
        e.Handled = true;
    }

    private void Resizer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var newWidth = _preManipulationWidth - e.Cumulative.Translation.X;
        newWidth = System.Math.Clamp(newWidth, MinPanelWidth, MaxPanelWidth);
        PanelWidth = newWidth;
        e.Handled = true;
    }

    private void Resizer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _draggingResizer = false;
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
        VisualStateManager.GoToState(this, "ResizerPointerOver", true);
        e.Handled = true;
    }

    private void Resizer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingResizer) return;

        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        IsOpen = !IsOpen;
        e.Handled = true;
    }
}
