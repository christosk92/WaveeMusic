using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Enums;

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
            view.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            view.UpdateTimerState();

            // When the panel opens, kick a deferred UpdateCanvasLayout so the
            // lyrics canvas picks up the now-real RootGrid dimensions. We pair
            // this with the IsOpen gate inside ScheduleCanvasLayoutRetry — that
            // gate prevents the closed-panel infinite retry loop, so we have to
            // re-prime the canvas explicitly when the panel becomes visible.
            if (isOpen && view.DispatcherQueue != null)
            {
                view.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (view.IsLoaded && view.IsOpen)
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
}
