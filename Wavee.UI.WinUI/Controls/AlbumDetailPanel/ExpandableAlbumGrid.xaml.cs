using System;
using System.Collections;
using System.Collections.Specialized;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.AlbumDetailPanel;

/// <summary>
/// A custom album grid that renders cards in rows and can insert an
/// expandable detail panel inline between any two rows (Apple Music style).
/// Manually manages the visual tree — no ItemsRepeater.
/// </summary>
public sealed partial class ExpandableAlbumGrid : UserControl
{
    private const double MinCardWidth = 160;
    private const double CardSpacing = 12;
    private int _columnsPerRow;
    private AlbumDetailPanel? _detailPanel;
    private int _expandedRowIndex = -1;
    private LazyReleaseItem? _expandedItem;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ExpandableAlbumGrid),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Raised when the expanded album changes (for ViewModel binding).</summary>
    public event EventHandler<LazyReleaseItem?>? ExpandedAlbumChanged;

    public ExpandableAlbumGrid()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (ExpandableAlbumGrid)d;

        // Unsubscribe from old collection
        if (e.OldValue is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= grid.OnCollectionChanged;

        // Subscribe to new collection
        if (e.NewValue is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += grid.OnCollectionChanged;

        grid.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var newCols = ComputeColumns(e.NewSize.Width);
        if (newCols != _columnsPerRow)
        {
            _columnsPerRow = newCols;
            Rebuild();
        }
    }

    private int ComputeColumns(double width)
    {
        if (width <= 0) return 4;
        return Math.Max(1, (int)Math.Floor((width + CardSpacing) / (MinCardWidth + CardSpacing)));
    }

    private void Rebuild()
    {
        RootPanel.Children.Clear();
        _detailPanel = null;
        _expandedRowIndex = -1;

        var items = ItemsSource;
        if (items == null) return;

        var list = new System.Collections.Generic.List<LazyReleaseItem>();
        foreach (var item in items)
        {
            if (item is LazyReleaseItem lri)
                list.Add(lri);
        }

        if (list.Count == 0) return;
        if (_columnsPerRow <= 0) _columnsPerRow = ComputeColumns(ActualWidth);

        // Build rows
        int rowIndex = 0;
        for (int i = 0; i < list.Count; i += _columnsPerRow)
        {
            var rowPanel = CreateRow(list, i, Math.Min(_columnsPerRow, list.Count - i));
            RootPanel.Children.Add(rowPanel);

            // Re-insert expanded detail if it was in this row
            if (_expandedItem != null && rowIndex == _expandedRowIndex)
            {
                EnsureDetailPanel();
                RootPanel.Children.Add(_detailPanel!);
            }

            rowIndex++;
        }
    }

    private Grid CreateRow(System.Collections.Generic.List<LazyReleaseItem> items, int startIndex, int count)
    {
        var row = new Grid { ColumnSpacing = CardSpacing };

        for (int i = 0; i < _columnsPerRow; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < count; i++)
        {
            var item = items[startIndex + i];
            var card = CreateCard(item);
            Grid.SetColumn(card, i);
            row.Children.Add(card);
        }

        return row;
    }

    private FrameworkElement CreateCard(LazyReleaseItem item)
    {
        if (!item.IsLoaded || item.Data == null)
        {
            // Shimmer placeholder
            var shimmerStack = new StackPanel { Spacing = 8, Padding = new Thickness(0) };
            shimmerStack.Children.Add(new Shimmer
            {
                Height = 150,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
            shimmerStack.Children.Add(new Shimmer
            {
                Width = 120, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            shimmerStack.Children.Add(new Shimmer
            {
                Width = 50, Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            return shimmerStack;
        }

        var contentCard = new ContentCard
        {
            ImageUrl = item.Data.ImageUrl,
            Title = item.Data.Name,
            Subtitle = item.Data.Year.ToString(),
        };

        // On click: expand/collapse this album
        contentCard.CardClick += (_, _) =>
        {
            ToggleExpand(item);
        };

        return contentCard;
    }

    private void ToggleExpand(LazyReleaseItem item)
    {
        if (_expandedItem?.Id == item.Id)
        {
            // Collapse
            _expandedItem = null;
            _expandedRowIndex = -1;
            ExpandedAlbumChanged?.Invoke(this, null);
            Rebuild();
            return;
        }

        // Find which row this item is in
        var items = new System.Collections.Generic.List<LazyReleaseItem>();
        if (ItemsSource != null)
        {
            foreach (var obj in ItemsSource)
            {
                if (obj is LazyReleaseItem lri) items.Add(lri);
            }
        }

        var index = items.IndexOf(item);
        if (index < 0) return;

        _expandedItem = item;
        _expandedRowIndex = index / _columnsPerRow;
        ExpandedAlbumChanged?.Invoke(this, item);
        Rebuild();
    }

    private void EnsureDetailPanel()
    {
        if (_detailPanel == null)
        {
            _detailPanel = new AlbumDetailPanel();
            _detailPanel.CloseRequested += (_, _) =>
            {
                _expandedItem = null;
                _expandedRowIndex = -1;
                ExpandedAlbumChanged?.Invoke(this, null);
                Rebuild();
            };
        }

        if (_expandedItem?.Data != null)
        {
            _detailPanel.Album = _expandedItem.Data;
        }
    }
}
