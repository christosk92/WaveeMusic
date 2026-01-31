using System;
using System.Collections;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
            new PropertyMetadata(false));

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

    #endregion

    public LibraryGridView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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
