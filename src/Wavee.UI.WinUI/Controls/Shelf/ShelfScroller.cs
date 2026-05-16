using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Shelf;

/// <summary>
/// Horizontal scrolling row of items. Does not render its own chevrons — the
/// containing header (see <see cref="SectionShelf"/>, or a page's own header
/// markup) is expected to place paging buttons bound to
/// <see cref="PageLeftCommand"/> / <see cref="PageRightCommand"/>, with
/// visibility driven by <see cref="HasOverflow"/>. Classic Spotify-desktop
/// pattern: chevrons live in the section header, not over the row.
/// </summary>
[TemplatePart(Name = PartScroll, Type = typeof(ScrollView))]
[TemplatePart(Name = PartRepeater, Type = typeof(ItemsRepeater))]
public sealed class ShelfScroller : Control
{
    private const string PartScroll = "PART_Scroll";
    private const string PartRepeater = "PART_Repeater";
    private const double EdgeEpsilon = 1.0;

    private ScrollView? _scroll;
    private ItemsRepeater? _repeater;
    private bool _pagingStatePostPending;
    private bool _hasItemsSourceIdentity;
    private bool _pendingIdentityRecycleReset;
    private bool _identityRecycleResetPosted;

    public ShelfScroller()
    {
        DefaultStyleKey = typeof(ShelfScroller);
        PageLeftCommand = new RelayCommand(PageLeft, () => CanPageLeft);
        PageRightCommand = new RelayCommand(PageRight, () => CanPageRight);

        // Wheel-over-shelf must scroll the host page, not be eaten by the
        // inner ScrollView. The new ScrollView's InteractionTracker captures
        // wheel input at the system input layer (before standard routed-event
        // handlers run), so IgnoredInputKinds="All" alone does not reliably
        // bubble the wheel to ancestor scrollers. Hook the event with
        // handledEventsToo so we catch it regardless of who else acted on it
        // and forward the delta to the nearest ancestor ScrollView.
        AddHandler(
            PointerWheelChangedEvent,
            new PointerEventHandler(OnPointerWheelChanged),
            handledEventsToo: true);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;

        DependencyObject? parent = VisualTreeHelper.GetParent(this);
        while (parent is not null)
        {
            if (parent is ScrollView ancestor)
            {
                // One wheel notch = 120 units. 96 px/notch feels close to a
                // browser; large enough to traverse a track row + small gap
                // per click without overshooting on rapid spins.
                const double NotchPixels = 96.0;
                var dy = -(delta / 120.0) * NotchPixels;
                ancestor.ScrollBy(0, dy);
                e.Handled = true;
                return;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
    }

    // ── Item-binding DPs ────────────────────────────────────────────────────

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(ShelfScroller),
            new PropertyMetadata(null, OnItemsSourceChanged));
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceIdentityProperty =
        DependencyProperty.Register(nameof(ItemsSourceIdentity), typeof(object), typeof(ShelfScroller),
            new PropertyMetadata(null, OnItemsSourceIdentityChanged));
    public object? ItemsSourceIdentity
    {
        get => GetValue(ItemsSourceIdentityProperty);
        set => SetValue(ItemsSourceIdentityProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(ShelfScroller),
            new PropertyMetadata(null, OnItemTemplateChanged));
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateSelectorProperty =
        DependencyProperty.Register(nameof(ItemTemplateSelector), typeof(DataTemplateSelector), typeof(ShelfScroller),
            new PropertyMetadata(null, OnItemTemplateSelectorChanged));
    public DataTemplateSelector? ItemTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(ItemTemplateSelectorProperty);
        set => SetValue(ItemTemplateSelectorProperty, value);
    }

    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(ShelfScroller),
            new PropertyMetadata(160.0, OnLayoutDimensionChanged));
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty MaxItemWidthProperty =
        DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(ShelfScroller),
            new PropertyMetadata(200.0));
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ShelfScroller),
            new PropertyMetadata(12.0, OnLayoutDimensionChanged));
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly DependencyProperty OverlapCardsProperty =
        DependencyProperty.Register(nameof(OverlapCards), typeof(int), typeof(ShelfScroller),
            new PropertyMetadata(1));
    public int OverlapCards
    {
        get => (int)GetValue(OverlapCardsProperty);
        set => SetValue(OverlapCardsProperty, value);
    }

    // ── Paging state DPs (read by chevron hosts) ───────────────────────────

    public static readonly DependencyProperty CanPageLeftProperty =
        DependencyProperty.Register(nameof(CanPageLeft), typeof(bool), typeof(ShelfScroller),
            new PropertyMetadata(false));
    public bool CanPageLeft
    {
        get => (bool)GetValue(CanPageLeftProperty);
        private set => SetValue(CanPageLeftProperty, value);
    }

    public static readonly DependencyProperty CanPageRightProperty =
        DependencyProperty.Register(nameof(CanPageRight), typeof(bool), typeof(ShelfScroller),
            new PropertyMetadata(false));
    public bool CanPageRight
    {
        get => (bool)GetValue(CanPageRightProperty);
        private set => SetValue(CanPageRightProperty, value);
    }

    public static readonly DependencyProperty HasOverflowProperty =
        DependencyProperty.Register(nameof(HasOverflow), typeof(bool), typeof(ShelfScroller),
            new PropertyMetadata(false));
    public bool HasOverflow
    {
        get => (bool)GetValue(HasOverflowProperty);
        private set => SetValue(HasOverflowProperty, value);
    }

    // ── Public paging API ──────────────────────────────────────────────────

    public ICommand PageLeftCommand { get; }
    public ICommand PageRightCommand { get; }

    public void PageLeft() => PageBy(-1);
    public void PageRight() => PageBy(1);

    // ── Template lifecycle ─────────────────────────────────────────────────

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_scroll is not null)
        {
            _scroll.ViewChanged -= OnViewChanged;
            _scroll.SizeChanged -= OnScrollSizeChanged;
        }
        if (_repeater is not null)
        {
            _repeater.SizeChanged -= OnScrollSizeChanged;
        }

        _scroll = GetTemplateChild(PartScroll) as ScrollView;
        _repeater = GetTemplateChild(PartRepeater) as ItemsRepeater;

        if (_repeater is not null)
        {
            _repeater.SizeChanged += OnScrollSizeChanged;
            _repeater.ItemTemplate = (object?)ItemTemplateSelector ?? ItemTemplate;
            _repeater.ItemsSource = ItemsSource;
            SyncLayoutProperties();
        }
        if (_scroll is not null)
        {
            _scroll.ViewChanged += OnViewChanged;
            _scroll.SizeChanged += OnScrollSizeChanged;
        }

        UpdatePagingState();
    }

    // ── DP change callbacks ────────────────────────────────────────────────

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ShelfScroller s || s._scroll is null) return;
        if (ReferenceEquals(e.OldValue, e.NewValue)) return;

        // CROSS-SECTION RECYCLE FIX
        // ─────────────────────────
        // When the outer SectionsRepeater recycles a section's container
        // from HomeSection A to HomeSection B, the inner shelf's
        // {x:Bind Items, Mode=OneWay} binding fires here with B.Items as
        // e.NewValue. ItemsRepeater's documented behaviour on ItemsSource
        // reassignment retains realized children at indices < min(oldCount,
        // newCount) and silently rebinds them — leaving x:Bind compiled
        // bindings inside those retained cards still pointing at A's items.
        // Earlier instrumentation confirmed no Clearing fires for those
        // retained children and Prepared only fires for indices >= oldCount.
        // Every public-API workaround (null-then-set, UpdateLayout pump,
        // ItemTemplate reassign, wrapper selector to bust the recycle pool)
        // failed to force teardown.
        //
        // Cross-section reuse still needs a fresh ItemsRepeater, but callers
        // that pass ItemsSourceIdentity let us avoid that cost for ordinary
        // same-section source updates.
        if (e.OldValue is null)
        {
            // First assignment (the one OnApplyTemplate makes after the
            // template part is wired). No prior realized state to worry
            // about; just forward.
            if (s._repeater is not null) s._repeater.ItemsSource = e.NewValue;
            return;
        }

        if (s.ItemsSourceIdentity is null)
        {
            s.RebuildRepeater(e.NewValue);
            return;
        }

        if (s._pendingIdentityRecycleReset)
        {
            s._pendingIdentityRecycleReset = false;
            s.RebuildRepeater(e.NewValue);
            return;
        }

        if (s._repeater is not null)
            s._repeater.ItemsSource = e.NewValue;
    }

    private static void OnItemsSourceIdentityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ShelfScroller s || Equals(e.OldValue, e.NewValue))
            return;

        if (!s._hasItemsSourceIdentity)
        {
            s._hasItemsSourceIdentity = true;
            return;
        }

        // x:Bind can update ItemsSource and ItemsSourceIdentity in either
        // order when an outer section container is recycled. Mark the recycle
        // here and post a reset so the final ItemsSource wins.
        s._pendingIdentityRecycleReset = true;
        s.ScheduleIdentityRecycleReset();
    }

    private void ScheduleIdentityRecycleReset()
    {
        if (_identityRecycleResetPosted)
            return;

        var queue = DispatcherQueue;
        if (queue is null)
        {
            ApplyPendingIdentityRecycleReset();
            return;
        }

        _identityRecycleResetPosted = true;
        if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _identityRecycleResetPosted = false;
            ApplyPendingIdentityRecycleReset();
        }))
        {
            _identityRecycleResetPosted = false;
            ApplyPendingIdentityRecycleReset();
        }
    }

    private void ApplyPendingIdentityRecycleReset()
    {
        if (!_pendingIdentityRecycleReset)
            return;

        _pendingIdentityRecycleReset = false;
        if (_scroll is not null && _repeater is not null)
            RebuildRepeater(ItemsSource);
    }

    private void RebuildRepeater(object? newItemsSource)
    {
        if (_scroll is null) return;

        if (_repeater is not null)
        {
            _repeater.SizeChanged -= OnScrollSizeChanged;
            // Drop the old repeater's ItemsSource so it releases its
            // collection-changed subscription before being detached.
            _repeater.ItemsSource = null;
        }

        var fresh = new ItemsRepeater
        {
            VerticalAlignment = VerticalAlignment.Top,
            Layout = new ShelfLayout(),
            ItemTemplate = (object?)ItemTemplateSelector ?? ItemTemplate,
        };
        _scroll.Content = fresh;
        _repeater = fresh;
        _repeater.SizeChanged += OnScrollSizeChanged;
        SyncLayoutProperties();
        _repeater.ItemsSource = newItemsSource;
    }

    private static void OnItemTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShelfScroller s && s._repeater is not null && s.ItemTemplateSelector is null)
            s._repeater.ItemTemplate = e.NewValue;
    }

    private static void OnItemTemplateSelectorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShelfScroller s && s._repeater is not null)
            s._repeater.ItemTemplate = e.NewValue ?? (object?)s.ItemTemplate;
    }

    private static void OnLayoutDimensionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShelfScroller s)
        {
            s.SyncLayoutProperties();
            s.UpdatePagingState();
        }
    }

    private void SyncLayoutProperties()
    {
        if (_repeater?.Layout is ShelfLayout layout)
        {
            layout.ItemWidth = MinItemWidth;
            layout.Spacing = Spacing;
        }
    }

    private void OnViewChanged(ScrollView sender, object args) => SchedulePagingStateUpdate();
    private void OnScrollSizeChanged(object? sender, SizeChangedEventArgs e) => SchedulePagingStateUpdate();

    /// <summary>
    /// Posts the paging-state refresh to the dispatcher so it never fires inline
    /// during a layout pass. If a post is already pending, coalesce — we only need
    /// the final state after the scroll settles. <see cref="HasOverflow"/> is bound
    /// to a chevron <c>Visibility</c> in page headers; flipping it mid-layout would
    /// re-invalidate the header's measure and cascade into a layout cycle.
    /// </summary>
    private void SchedulePagingStateUpdate()
    {
        if (_pagingStatePostPending) return;
        var queue = DispatcherQueue;
        if (queue is null)
        {
            UpdatePagingState();
            return;
        }

        _pagingStatePostPending = true;
        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pagingStatePostPending = false;
            UpdatePagingState();
        }))
        {
            _pagingStatePostPending = false;
        }
    }

    // ── Paging math ────────────────────────────────────────────────────────

    private void PageBy(int direction)
    {
        if (_scroll is null) return;

        var step = MinItemWidth + Spacing;
        if (step <= 0) return;

        var viewport = _scroll.ViewportWidth;
        var overlap = Math.Max(0, OverlapCards) * step;
        // Page by viewport width minus overlap, but never less than one card step —
        // if the viewport is narrower than one card we still want chevrons to
        // advance meaningfully.
        var delta = Math.Max(step, viewport - overlap);
        var target = SnapToCardEdge(_scroll.HorizontalOffset + direction * delta);
        var options = new ScrollingScrollOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore);
        _scroll.ScrollTo(target, _scroll.VerticalOffset, options);
    }

    private double SnapToCardEdge(double offset)
    {
        if (_scroll is null) return offset;
        var step = MinItemWidth + Spacing;
        if (step <= 0) return offset;
        var snapped = Math.Round(offset / step) * step;
        var max = Math.Max(0, _scroll.ExtentWidth - _scroll.ViewportWidth);
        return Math.Clamp(snapped, 0, max);
    }

    private void UpdatePagingState()
    {
        if (_scroll is null)
        {
            HasOverflow = false;
            CanPageLeft = false;
            CanPageRight = false;
        }
        else
        {
            HasOverflow = _scroll.ExtentWidth > _scroll.ViewportWidth + EdgeEpsilon;
            CanPageLeft = HasOverflow && _scroll.HorizontalOffset > EdgeEpsilon;
            CanPageRight = HasOverflow
                && (_scroll.HorizontalOffset + _scroll.ViewportWidth) < (_scroll.ExtentWidth - EdgeEpsilon);
        }

        (PageLeftCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (PageRightCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
}
