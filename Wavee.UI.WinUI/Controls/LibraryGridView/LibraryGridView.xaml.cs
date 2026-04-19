using System;
using System.Collections;
using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// A reusable control for library grid views with search, item grid, and detail panel.
/// </summary>
public sealed partial class LibraryGridView : UserControl
{
    #region Dependency Properties

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(LibraryGridView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(LibraryGridView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DetailContentProperty =
        DependencyProperty.Register(nameof(DetailContent), typeof(object), typeof(LibraryGridView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(LibraryGridView),
            new PropertyMetadata(null, OnSelectedItemChanged));

    public static readonly DependencyProperty SearchQueryProperty =
        DependencyProperty.Register(nameof(SearchQuery), typeof(string), typeof(LibraryGridView),
            new PropertyMetadata("", OnSearchQueryChanged));

    public static readonly DependencyProperty SearchPlaceholderTextProperty =
        DependencyProperty.Register(nameof(SearchPlaceholderText), typeof(string), typeof(LibraryGridView),
            new PropertyMetadata("Filter items..."));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(LibraryGridView),
            new PropertyMetadata(false, OnIsLoadingChanged));

    public static readonly DependencyProperty EmptyStateTextProperty =
        DependencyProperty.Register(nameof(EmptyStateText), typeof(string), typeof(LibraryGridView),
            new PropertyMetadata("Select an item to see details"));

    public static readonly DependencyProperty EmptyStateGlyphProperty =
        DependencyProperty.Register(nameof(EmptyStateGlyph), typeof(string), typeof(LibraryGridView),
            new PropertyMetadata("\uE8B9")); // Default info icon

    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(LibraryGridView),
            new PropertyMetadata(160.0));

    public static readonly DependencyProperty MinItemHeightProperty =
        DependencyProperty.Register(nameof(MinItemHeight), typeof(double), typeof(LibraryGridView),
            new PropertyMetadata(210.0));

    public static readonly DependencyProperty SelectionChangedCommandProperty =
        DependencyProperty.Register(nameof(SelectionChangedCommand), typeof(ICommand), typeof(LibraryGridView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderLeadingContentProperty =
        DependencyProperty.Register(nameof(HeaderLeadingContent), typeof(object), typeof(LibraryGridView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SubHeaderContentProperty =
        DependencyProperty.Register(nameof(SubHeaderContent), typeof(object), typeof(LibraryGridView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ViewModeProperty =
        DependencyProperty.Register(nameof(ViewMode), typeof(LibraryViewMode), typeof(LibraryGridView),
            new PropertyMetadata(LibraryViewMode.DefaultGrid, OnViewModeChanged));

    public static readonly DependencyProperty CompactListItemTemplateProperty =
        DependencyProperty.Register(nameof(CompactListItemTemplate), typeof(DataTemplate), typeof(LibraryGridView),
            new PropertyMetadata(null, OnTemplatePropertyChanged));

    public static readonly DependencyProperty DefaultListItemTemplateProperty =
        DependencyProperty.Register(nameof(DefaultListItemTemplate), typeof(DataTemplate), typeof(LibraryGridView),
            new PropertyMetadata(null, OnTemplatePropertyChanged));

    public static readonly DependencyProperty CompactGridItemTemplateProperty =
        DependencyProperty.Register(nameof(CompactGridItemTemplate), typeof(DataTemplate), typeof(LibraryGridView),
            new PropertyMetadata(null, OnTemplatePropertyChanged));

    public static readonly DependencyProperty DefaultGridItemTemplateProperty =
        DependencyProperty.Register(nameof(DefaultGridItemTemplate), typeof(DataTemplate), typeof(LibraryGridView),
            new PropertyMetadata(null, OnTemplatePropertyChanged));

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets the collection of items to display in the grid.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Gets or sets the data template for grid items.
    /// </summary>
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>
    /// Gets or sets the content for the detail panel (shown when an item is selected).
    /// </summary>
    public object? DetailContent
    {
        get => GetValue(DetailContentProperty);
        set => SetValue(DetailContentProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently selected item.
    /// </summary>
    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>
    /// Gets or sets the search query text.
    /// </summary>
    public string SearchQuery
    {
        get => (string)GetValue(SearchQueryProperty);
        set => SetValue(SearchQueryProperty, value);
    }

    /// <summary>
    /// Gets or sets the placeholder text for the search box.
    /// </summary>
    public string SearchPlaceholderText
    {
        get => (string)GetValue(SearchPlaceholderTextProperty);
        set => SetValue(SearchPlaceholderTextProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the control is in a loading state.
    /// </summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>
    /// Gets or sets the text shown in the empty state.
    /// </summary>
    public string EmptyStateText
    {
        get => (string)GetValue(EmptyStateTextProperty);
        set => SetValue(EmptyStateTextProperty, value);
    }

    /// <summary>
    /// Gets or sets the glyph icon for the empty state.
    /// </summary>
    public string EmptyStateGlyph
    {
        get => (string)GetValue(EmptyStateGlyphProperty);
        set => SetValue(EmptyStateGlyphProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum width for grid items.
    /// </summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum height for grid items.
    /// </summary>
    public double MinItemHeight
    {
        get => (double)GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the command to execute when selection changes.
    /// </summary>
    public ICommand? SelectionChangedCommand
    {
        get => (ICommand?)GetValue(SelectionChangedCommandProperty);
        set => SetValue(SelectionChangedCommandProperty, value);
    }

    /// <summary>
    /// Content hosted to the left of the search box (e.g. a "Sort & view" trigger button).
    /// </summary>
    public object? HeaderLeadingContent
    {
        get => GetValue(HeaderLeadingContentProperty);
        set => SetValue(HeaderLeadingContentProperty, value);
    }

    /// <summary>
    /// Content placed between the search row and the items grid (e.g. an inline
    /// expandable sort/view panel).
    /// </summary>
    public object? SubHeaderContent
    {
        get => GetValue(SubHeaderContentProperty);
        set => SetValue(SubHeaderContentProperty, value);
    }

    /// <summary>
    /// Layout mode for items. Swaps between StackLayout (compact/default list) and
    /// UniformGridLayout (compact/default grid). The matching <c>*ItemTemplate</c> DP
    /// is applied at the same time; <see cref="ItemTemplate"/> is the fallback when
    /// a mode-specific template is not provided.
    /// </summary>
    public LibraryViewMode ViewMode
    {
        get => (LibraryViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    public DataTemplate? CompactListItemTemplate
    {
        get => (DataTemplate?)GetValue(CompactListItemTemplateProperty);
        set => SetValue(CompactListItemTemplateProperty, value);
    }

    public DataTemplate? DefaultListItemTemplate
    {
        get => (DataTemplate?)GetValue(DefaultListItemTemplateProperty);
        set => SetValue(DefaultListItemTemplateProperty, value);
    }

    public DataTemplate? CompactGridItemTemplate
    {
        get => (DataTemplate?)GetValue(CompactGridItemTemplateProperty);
        set => SetValue(CompactGridItemTemplateProperty, value);
    }

    public DataTemplate? DefaultGridItemTemplate
    {
        get => (DataTemplate?)GetValue(DefaultGridItemTemplateProperty);
        set => SetValue(DefaultGridItemTemplateProperty, value);
    }

    #endregion

    #region Events

    /// <summary>
    /// Occurs when the selected item changes.
    /// </summary>
    public event EventHandler<object?>? SelectionChanged;

    /// <summary>
    /// Occurs when the search query changes.
    /// </summary>
    public event EventHandler<string>? SearchQueryChanged;

    /// <summary>
    /// Occurs when an item is double-tapped (for direct navigation).
    /// </summary>
    public event EventHandler<object?>? ItemDoubleTapped;

    #endregion

    // Placeholder items to drive the shimmer ItemsRepeater DataTemplate
    private static readonly object[] ShimmerPlaceholders = Enumerable.Range(0, 8).Cast<object>().ToArray();

    public LibraryGridView()
    {
        InitializeComponent();
        ShimmerOverlay.ItemsSource = ShimmerPlaceholders;
        Loaded += OnLoaded;
        ItemsGridView.DoubleTapped += ItemsGridView_DoubleTapped;

        // Apply the initial ViewMode / template once named elements exist.
        ApplyViewMode();
    }

    private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LibraryGridView control)
            control.ApplyViewMode();
    }

    private static void OnTemplatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Mode-specific templates may arrive after ViewMode is set (they live on the
        // consumer's XAML and are assigned sequentially). Re-apply whenever any
        // template DP changes so the currently-selected mode picks up a late binding.
        if (d is LibraryGridView control)
            control.ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (ItemsGridView == null) return;

        var (template, layout, minWidth, minHeight) = ViewMode switch
        {
            LibraryViewMode.CompactList => (
                CompactListItemTemplate ?? DefaultListItemTemplate ?? ItemTemplate,
                (Microsoft.UI.Xaml.Controls.Layout)new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 },
                (double)0,
                (double)36),
            LibraryViewMode.DefaultList => (
                DefaultListItemTemplate ?? ItemTemplate,
                new StackLayout { Orientation = Orientation.Vertical, Spacing = 4 },
                (double)0,
                (double)56),
            LibraryViewMode.CompactGrid => (
                CompactGridItemTemplate ?? DefaultGridItemTemplate ?? ItemTemplate,
                new UniformGridLayout
                {
                    MinItemWidth = 100,
                    MinItemHeight = 100,
                    MinRowSpacing = 8,
                    MinColumnSpacing = 8,
                    ItemsStretch = UniformGridLayoutItemsStretch.Uniform
                },
                (double)100,
                (double)100),
            _ => (
                DefaultGridItemTemplate ?? ItemTemplate,
                new UniformGridLayout
                {
                    MinItemWidth = MinItemWidth,
                    MinItemHeight = MinItemHeight,
                    MinRowSpacing = 12,
                    MinColumnSpacing = 12,
                    // Cells size to the card's natural height so an optional badge (e.g.
                    // "Played 3h ago") is included without the consumer having to bump
                    // MinItemHeight. MinItemWidth still floors the horizontal cell size.
                    ItemsStretch = UniformGridLayoutItemsStretch.None
                },
                MinItemWidth,
                MinItemHeight)
        };

        // Keep the shimmer aligned to the items layout so the placeholder silhouette
        // approximates the actual rendered rows/cards.
        if (layout is UniformGridLayout uniform)
        {
            ShimmerOverlay.Layout = new UniformGridLayout
            {
                MinItemWidth = uniform.MinItemWidth,
                MinItemHeight = uniform.MinItemHeight,
                MinRowSpacing = uniform.MinRowSpacing,
                MinColumnSpacing = uniform.MinColumnSpacing,
                ItemsStretch = uniform.ItemsStretch
            };
        }
        else if (layout is StackLayout stack)
        {
            ShimmerOverlay.Layout = new StackLayout
            {
                Orientation = stack.Orientation,
                Spacing = stack.Spacing
            };
        }

        ItemsGridView.Layout = layout;
        if (template != null)
            ItemsGridView.ItemTemplate = template;
        _ = minWidth; _ = minHeight;
    }

    private void ItemsGridView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (SelectedItem == null) return;

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // // Prepare connected animation from the tapped card's image
        // if (e.OriginalSource is DependencyObject source)
        // {
        //     var card = FindParent<Cards.ContentCard>(source);
        //     card?.PrepareConnectedAnimation();
        // }

        ItemDoubleTapped?.Invoke(this, SelectedItem);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T match) return match;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Sync selection when control is loaded (important for page cache restoration)
        SyncSelectionToItemsView();
    }

    private void SyncSelectionToItemsView()
    {
        if (ItemsGridView is null) return;

        if (SelectedItem is null)
        {
            ItemsGridView.DeselectAll();
        }
        else if (ItemsSource is IList list)
        {
            var index = list.IndexOf(SelectedItem);
            if (index >= 0 && ItemsGridView.SelectedItem != SelectedItem)
            {
                ItemsGridView.Select(index);
            }
        }
    }

    private void ItemsGridView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem != SelectedItem)
        {
            SelectedItem = sender.SelectedItem;
        }
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LibraryGridView control && e.NewValue is bool loading)
        {
            control.ShimmerOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            control.ItemsGridView.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LibraryGridView control)
        {
            control.SyncSelectionToItemsView();
            control.SelectionChanged?.Invoke(control, e.NewValue);
            control.SelectionChangedCommand?.Execute(e.NewValue);
        }
    }

    private static void OnSearchQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LibraryGridView control && e.NewValue is string newQuery)
        {
            control.SearchQueryChanged?.Invoke(control, newQuery);
        }
    }
}
