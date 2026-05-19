using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Controls.RightPanel;

/// <summary>
/// The tab-header strip for <see cref="RightPanelView"/> — a horizontally
/// scrollable <c>Segmented</c> flanked by chevron pager buttons and a pop-out
/// affordance.
/// </summary>
/// <remarks>
/// <para>
/// Why a sub-control: localised tab labels (Korean "세부 정보", etc.) can
/// overflow the panel width. The Segmented sits inside a ScrollViewer, and
/// chevron visibility tracks scroll position. Lifting that whole subtree out
/// of <see cref="RightPanelView"/> means the parent stops carrying eight tab
/// names, four event handlers, and the "initial selection settled" race
/// guard.
/// </para>
/// <para>
/// The initial-selection race: the inner <c>Segmented</c> ships with
/// <c>SelectedIndex="0"</c> (Queue). When XAML loads, that fires
/// <c>SelectionChanged</c> before the parent's two-way <c>SelectedMode</c>
/// binding has pushed the persisted/requested mode (e.g. Lyrics). The result
/// is that Queue silently overwrites the user's intent. The suppression flag
/// holds <c>SelectionChanged</c> off until <see cref="ReleaseInitialSelectionSuppression"/>
/// is called from the parent's <c>Loaded</c>.
/// </para>
/// </remarks>
public sealed partial class RightPanelTabPager : UserControl
{
    private const double TabPagerScrollStepPx = 96;

    private bool _suppressTabHeaderSelectionChanged = true;

    // Re-entry guard for OnSelectedModeChanged. The chain
    // ShellViewModel.RightPanelMode ↔ RightPanelView.SelectedMode ↔
    // RightPanelTabPager.SelectedMode ↔ Segmented.SelectedItem can bounce
    // through async SelectionChanged firings: the suppression flag in
    // SyncVisualState only covers the synchronous SelectedItem assignment,
    // and if SelectionChanged fires after `finally` clears the flag, the
    // handler pushes the value back through the TwoWay binding chain. This
    // guard short-circuits the re-entry at the DP callback so we never
    // recurse into SetValue.
    private bool _inSelectedModeChange;

    public RightPanelTabPager()
    {
        InitializeComponent();
    }

    public RightPanelMode SelectedMode
    {
        get => (RightPanelMode)GetValue(SelectedModeProperty);
        // Short-circuit equal writes: WinUI's DP system skips the change
        // callback for equal values, but a TwoWay binding still propagates
        // the SetValue *write* to the bound source on every assignment. With
        // chained bindings (ShellVM.RightPanelMode ↔ RightPanelView.SelectedMode
        // ↔ TabPager.SelectedMode), an equal-value write bounces through all
        // three setters and back, exhausting the stack. Refusing SetValue
        // for an equal value breaks the cycle at the source.
        set
        {
            if ((RightPanelMode)GetValue(SelectedModeProperty) == value) return;
            SetValue(SelectedModeProperty, value);
        }
    }
    public static readonly DependencyProperty SelectedModeProperty =
        DependencyProperty.Register(
            nameof(SelectedMode),
            typeof(RightPanelMode),
            typeof(RightPanelTabPager),
            new PropertyMetadata(RightPanelMode.Queue, OnSelectedModeChanged));

    /// <summary>
    /// Show or hide the temporary "Track details" segment. The parent toggles
    /// it whenever <c>ShellViewModel.SelectedTrackForDetails</c> changes, so
    /// the segment only exists while a track is being inspected.
    /// </summary>
    public bool IsTrackDetailsTabVisible
    {
        get => (bool)GetValue(IsTrackDetailsTabVisibleProperty);
        set => SetValue(IsTrackDetailsTabVisibleProperty, value);
    }
    public static readonly DependencyProperty IsTrackDetailsTabVisibleProperty =
        DependencyProperty.Register(
            nameof(IsTrackDetailsTabVisible),
            typeof(bool),
            typeof(RightPanelTabPager),
            new PropertyMetadata(false, OnIsTrackDetailsTabVisibleChanged));

    /// <summary>
    /// Label shown on the lyrics tab — flips between "Lyrics" and "Transcript"
    /// depending on whether the currently-playing item is a music track or a
    /// podcast episode. The parent <see cref="RightPanelView"/> writes this in
    /// response to <c>LyricsViewModel.IsEpisode</c> changes. Default mirrors the
    /// XAML literal so first paint never shows an empty tab.
    /// </summary>
    public string LyricsTabContent
    {
        get => (string)GetValue(LyricsTabContentProperty);
        set => SetValue(LyricsTabContentProperty, value);
    }
    public static readonly DependencyProperty LyricsTabContentProperty =
        DependencyProperty.Register(
            nameof(LyricsTabContent),
            typeof(string),
            typeof(RightPanelTabPager),
            new PropertyMetadata("Lyrics", OnLyricsTabContentChanged));

    private static void OnLyricsTabContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelTabPager pager && pager.LyricsTabItem != null)
            pager.LyricsTabItem.Content = e.NewValue as string ?? "Lyrics";
    }

    /// <summary>
    /// Raised after the user clicks the pop-out button. The parent listens and
    /// dispatches to <c>IPanelDockingService.Detach</c>; this control stays
    /// service-agnostic.
    /// </summary>
    public event EventHandler? PopOutRequested;

    /// <summary>
    /// Releases the suppression flag set during construction (see remarks on
    /// the class). The parent calls this from its <c>Loaded</c> handler once
    /// the <c>SelectedMode</c> two-way binding has settled.
    /// </summary>
    public void ReleaseInitialSelectionSuppression()
    {
        _suppressTabHeaderSelectionChanged = false;
    }

    /// <summary>
    /// Force the visible segment to match the current <see cref="SelectedMode"/>.
    /// Used by the parent after a theme refresh or other indirect state change
    /// that could leave the visual selection stale.
    /// </summary>
    public void SyncVisualState()
    {
        if (TabHeader == null) return;

        var targetItem = SelectedMode switch
        {
            RightPanelMode.Queue => QueueTabItem,
            RightPanelMode.Lyrics => LyricsTabItem,
            RightPanelMode.FriendsActivity => FriendsTabItem,
            RightPanelMode.Details => DetailsTabItem,
            RightPanelMode.TrackDetails => TrackDetailsTabItem,
            _ => QueueTabItem
        };

        if (ReferenceEquals(TabHeader.SelectedItem, targetItem))
            return;

        _suppressTabHeaderSelectionChanged = true;
        try
        {
            TabHeader.SelectedItem = targetItem;
        }
        finally
        {
            _suppressTabHeaderSelectionChanged = false;
        }
    }

    private static void OnSelectedModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RightPanelTabPager pager || pager._inSelectedModeChange)
            return;

        pager._inSelectedModeChange = true;
        try
        {
            pager.SyncVisualState();
        }
        finally
        {
            pager._inSelectedModeChange = false;
        }
    }

    private static void OnIsTrackDetailsTabVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightPanelTabPager pager && pager.TrackDetailsTabItem != null)
            pager.TrackDetailsTabItem.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Tab header pager + edge fades ───────────────────────────────────────
    // Long localized labels (e.g. Korean "세부 정보") can overflow the panel
    // width. The Segmented sits inside a horizontal ScrollViewer; these
    // handlers drive the chevron pager visibility based on scroll position.

    private void TabHeaderScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateTabHeaderEdgeAffordances();

    private void TabHeaderScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTabHeaderEdgeAffordances();

    private void UpdateTabHeaderEdgeAffordances()
    {
        if (TabHeaderScroller == null) return;

        double max = TabHeaderScroller.ScrollableWidth;
        double off = TabHeaderScroller.HorizontalOffset;

        const double eps = 0.5;
        bool overflowsLeft = off > eps;
        bool overflowsRight = max - off > eps;

        if (TabPagerLeftButton != null)
            TabPagerLeftButton.Visibility = overflowsLeft ? Visibility.Visible : Visibility.Collapsed;
        if (TabPagerRightButton != null)
            TabPagerRightButton.Visibility = overflowsRight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabPagerLeft_Click(object sender, RoutedEventArgs e)
    {
        if (TabHeaderScroller == null) return;
        double target = Math.Max(0, TabHeaderScroller.HorizontalOffset - TabPagerScrollStepPx);
        TabHeaderScroller.ChangeView(target, null, null);
    }

    private void TabPagerRight_Click(object sender, RoutedEventArgs e)
    {
        if (TabHeaderScroller == null) return;
        double target = Math.Min(TabHeaderScroller.ScrollableWidth, TabHeaderScroller.HorizontalOffset + TabPagerScrollStepPx);
        TabHeaderScroller.ChangeView(target, null, null);
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        PopOutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TabHeader_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabHeaderSelectionChanged || TabHeader?.SelectedItem is not SegmentedItem selectedItem)
            return;

        var nextMode = selectedItem.Tag switch
        {
            "Queue" => RightPanelMode.Queue,
            "Lyrics" => RightPanelMode.Lyrics,
            "Friends" => RightPanelMode.FriendsActivity,
            "Details" => RightPanelMode.Details,
            "TrackDetails" => RightPanelMode.TrackDetails,
            _ => RightPanelMode.Queue
        };

        // Idempotent: if SelectionChanged fires for a tab the binding has
        // already pushed us to (e.g. async tail of SyncVisualState's
        // SelectedItem assignment), don't write SelectedMode again — that
        // would re-enter OnSelectedModeChanged → SyncVisualState pointlessly.
        if (nextMode == SelectedMode)
            return;

        SelectedMode = nextMode;
    }
}
