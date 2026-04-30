using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Data.Enums;
using Windows.UI;
using Microsoft.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView
{
    public double PanelWidth
    {
        get => (double)GetValue(PanelWidthProperty);
        set => SetValue(PanelWidthProperty, value);
    }
    public static readonly DependencyProperty PanelWidthProperty =
        DependencyProperty.Register(nameof(PanelWidth), typeof(double), typeof(RightPanelView),
            new PropertyMetadata(300d, OnPanelWidthChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(RightPanelView),
            new PropertyMetadata(false, OnIsOpenChanged));

    public RightPanelMode SelectedMode
    {
        get => (RightPanelMode)GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }
    public static readonly DependencyProperty SelectedModeProperty =
        DependencyProperty.Register(nameof(SelectedMode), typeof(RightPanelMode), typeof(RightPanelView),
            new PropertyMetadata(RightPanelMode.Queue, OnSelectedModeChanged));

    /// <summary>
    /// Show/hide the segmented tab header at the top of the panel. Default
    /// <c>true</c>. Hosts that supply their own mode switcher (the floating
    /// player's expanded layout) set this <c>false</c> so the tab strip
    /// doesn't duplicate the bottom-right toggle buttons.
    /// </summary>
    public bool IsTabHeaderVisible
    {
        get => (bool)GetValue(IsTabHeaderVisibleProperty);
        set => SetValue(IsTabHeaderVisibleProperty, value);
    }
    public static readonly DependencyProperty IsTabHeaderVisibleProperty =
        DependencyProperty.Register(nameof(IsTabHeaderVisible), typeof(bool), typeof(RightPanelView),
            new PropertyMetadata(true, OnIsTabHeaderVisibleChanged));

    /// <summary>
    /// Hosts such as the detached fullscreen player supply their own background
    /// and mode controls. This suppresses the panel's local chrome so lyrics and
    /// queue render over the host surface instead of a standalone panel fill.
    /// </summary>
    public bool IsEmbeddedChromeTransparent
    {
        get => (bool)GetValue(IsEmbeddedChromeTransparentProperty);
        set => SetValue(IsEmbeddedChromeTransparentProperty, value);
    }
    public static readonly DependencyProperty IsEmbeddedChromeTransparentProperty =
        DependencyProperty.Register(nameof(IsEmbeddedChromeTransparent), typeof(bool), typeof(RightPanelView),
            new PropertyMetadata(false, OnIsEmbeddedChromeTransparentChanged));

    private static void OnPanelWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelView view)
            view.Width = (double)e.NewValue;
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelView view)
        {
            var isOpen = (bool)e.NewValue;
            view._isOpenCached = isOpen;
            view.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            view.UpdateTimerState();
            if (isOpen)
            {
                view.UpdateContentVisibility();
            }
            else
            {
                view.UpdateLyricsConsumerActivity(active: false);
                view.TeardownLyrics();
                view.CancelBackgroundTintRefresh();
            }

            // When the panel opens, kick a deferred UpdateCanvasLayout so the
            // lyrics canvas picks up the now-real RootGrid dimensions. We pair
            // this with the IsOpen gate inside ScheduleCanvasLayoutRetry — that
            // gate prevents the closed-panel infinite retry loop, so we have to
            // re-prime the canvas explicitly when the panel becomes visible.
            if (isOpen && view.DispatcherQueue != null)
            {
                view.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (view.IsLoaded && view._isOpenCached)
                        view.UpdateCanvasLayout();
                });
            }
        }
    }

    private static void OnSelectedModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelView view)
            view.UpdateContentVisibility();
    }

    private static void OnIsTabHeaderVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelView view)
            view.ApplyTabHeaderVisibility();
    }

    private static void OnIsEmbeddedChromeTransparentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelView view)
            view.ApplyEmbeddedChrome();
    }

    private void ApplyEmbeddedChrome()
    {
        if (RootGrid == null) return;

        RootGrid.Background = IsEmbeddedChromeTransparent
            ? new SolidColorBrush(Colors.Transparent)
            : null;

        if (PanelResizer != null)
            PanelResizer.Visibility = IsEmbeddedChromeTransparent ? Visibility.Collapsed : Visibility.Visible;

        if (BackgroundOverlayHost != null && IsEmbeddedChromeTransparent)
            BackgroundOverlayHost.Visibility = Visibility.Collapsed;

        if (DetailsCanvasImage != null && IsEmbeddedChromeTransparent)
            DetailsCanvasImage.Visibility = Visibility.Collapsed;

        UpdateCanvasClearColor();
        UpdateBackgroundChrome();
    }
}
