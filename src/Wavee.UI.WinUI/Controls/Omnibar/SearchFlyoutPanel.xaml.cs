using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.Omnibar;

public sealed partial class SearchFlyoutPanel : UserControl
{
    // Estimated row height used only to nudge the scroll viewer toward an
    // off-screen keyboard target before it has been realized; the precise
    // bring-into-view happens once the element is prepared.
    private const double EstimatedRowHeight = 52;

    private string _queryText = "";
    private int _keyboardIndex = -1; // -1 = nothing selected via keyboard

    // The single flat list bound to the ItemsRepeater. Grouped (three-section)
    // mode flattens its groups into this with interleaved SectionHeader rows;
    // recent-searches / no-match mode binds its items directly. One path.
    private List<SearchSuggestionItem> _items = new();

    private FrameworkElement? _highlightedElement;
    private Brush? _highlightBrush;
    private readonly Brush _transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public event EventHandler<SearchSuggestionItem>? ItemClicked;
    public event EventHandler<SearchSuggestionItem>? ActionClicked;
    public event EventHandler? RetryRequested;
    public event EventHandler? SuggestionContextMenuOpened;
    public event EventHandler? SuggestionContextMenuClosed;

    private static readonly DependencyProperty EntityDragAttachedProperty =
        DependencyProperty.RegisterAttached(
            "EntityDragAttached",
            typeof(bool),
            typeof(SearchFlyoutPanel),
            new PropertyMetadata(false));

    public SearchFlyoutPanel()
    {
        InitializeComponent();
        ResultsList.ElementPrepared += OnElementPrepared;
        ResultsList.ElementClearing += OnElementClearing;
    }

    private Brush HighlightBrush =>
        _highlightBrush ??= (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

    // ── Keyboard navigation ──────────────────────────────────────────────────

    /// <summary>
    /// Moves the keyboard highlight up or down over the flat list, skipping
    /// non-selectable rows (section headers, shimmer placeholders). Returns the
    /// highlighted item (or null). Does NOT change the search box text.
    /// </summary>
    public SearchSuggestionItem? MoveSelection(int delta)
    {
        if (_items.Count == 0)
            return null;

        var step = delta >= 0 ? 1 : -1;
        var candidate = _keyboardIndex + (delta == 0 ? 0 : step);
        while (true)
        {
            if (candidate < -1) { candidate = -1; break; }
            if (candidate >= _items.Count) { candidate = -1; break; }
            if (candidate == -1) break;
            if (!IsNonSelectable(_items[candidate].Type)) break;
            candidate += step;
        }

        _keyboardIndex = candidate;
        ApplyKeyboardHighlight();
        return _keyboardIndex >= 0 ? _items[_keyboardIndex] : null;
    }

    public SearchSuggestionItem? GetSelectedItem()
        => _keyboardIndex >= 0 && _keyboardIndex < _items.Count ? _items[_keyboardIndex] : null;

    public void ResetSelection()
    {
        _keyboardIndex = -1;
        ApplyKeyboardHighlight();
    }

    private static bool IsNonSelectable(SearchSuggestionType type)
        => type == SearchSuggestionType.Shimmer
        || type == SearchSuggestionType.SectionHeader;

    private void ApplyKeyboardHighlight()
    {
        // Clear the previously highlighted row.
        if (_highlightedElement is not null)
        {
            SetRowBackground(_highlightedElement, _transparentBrush);
            _highlightedElement = null;
        }

        if (_keyboardIndex < 0)
            return;

        if (ResultsList.TryGetElement(_keyboardIndex) is FrameworkElement el)
        {
            SetRowBackground(el, HighlightBrush);
            _highlightedElement = el;
            el.StartBringIntoView();
        }
        else
        {
            // Target is virtualized off-screen — nudge the scroll viewer toward it;
            // OnElementPrepared paints the highlight once the row realizes.
            ResultsScroller.ChangeView(null, _keyboardIndex * EstimatedRowHeight, null, true);
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // ItemsRepeater (unlike ListView) does not set the realized element's
        // DataContext to the data item, and our x:Bind templates don't either —
        // so the per-row pointer handlers that read sender.DataContext
        // (Item_Tapped, ActionButton_Click, EntityRoot_RightTapped/Holding, and the
        // EntityRoot_Loaded drag payload factory) were getting a non-item DataContext
        // and bailing. That's why mouse click / the action button / right-click / drag
        // did nothing while keyboard nav (which reads the _items model directly) worked.
        // Assign it here so every pointer path resolves the bound item.
        if (args.Index >= 0 && args.Index < _items.Count && args.Element is FrameworkElement fe)
            fe.DataContext = _items[args.Index];

        if (args.Index == _keyboardIndex && args.Element is FrameworkElement el)
        {
            SetRowBackground(el, HighlightBrush);
            _highlightedElement = el;
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        // Recycled elements must drop any highlight/hover tint so it doesn't bleed
        // onto the next item they're reused for.
        if (ReferenceEquals(args.Element, _highlightedElement))
            _highlightedElement = null;
        if (args.Element is FrameworkElement el)
            SetRowBackground(el, _transparentBrush);
    }

    private static void SetRowBackground(FrameworkElement el, Brush brush)
    {
        if (el is Panel p)
            p.Background = brush;
    }

    // ── States ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows the shimmer loading state immediately while data is being fetched.
    /// Fades the results (rather than collapsing) so realized rows survive and
    /// can be reused by the next <see cref="SetItems"/> / <see cref="SetGroups"/>.
    /// </summary>
    public void ShowShimmer(bool isRecentSearches)
    {
        if (ShimmerPanel.Visibility == Visibility.Visible)
        {
            HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsScroller.Opacity = 0;
        ResultsScroller.IsHitTestVisible = false;
    }

    /// <summary>
    /// Flat-list path — recent-searches mode and the no-match fallback.
    /// </summary>
    public void SetItems(List<SearchSuggestionItem>? items, string queryText, bool isRecentSearches)
    {
        _queryText = queryText;
        HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsScroller.Opacity = 1;
        ResultsScroller.IsHitTestVisible = true;
        ResultsScroller.Visibility = Visibility.Visible;

        _items = items ?? new List<SearchSuggestionItem>();
        ResultsList.ItemsSource = _items;
        ResetSelection();
        PrefetchAlbumSuggestions(_items);
    }

    /// <summary>
    /// Three-section path (Settings / Your library / Spotify). Flattens the groups
    /// into the single bound list with a non-interactive <c>SectionHeader</c> row
    /// before each non-empty group.
    /// </summary>
    public void SetGroups(IReadOnlyList<SearchSuggestionGroup>? groups, string queryText)
    {
        _queryText = queryText;
        HeaderText.Visibility = Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsScroller.Opacity = 1;
        ResultsScroller.IsHitTestVisible = true;
        ResultsScroller.Visibility = Visibility.Visible;

        _items = Flatten(groups);
        ResultsList.ItemsSource = _items;

        if (groups is not null)
            foreach (var group in groups)
                PrefetchAlbumSuggestions(group);

        ResetSelection();
    }

    private static List<SearchSuggestionItem> Flatten(IReadOnlyList<SearchSuggestionGroup>? groups)
    {
        var list = new List<SearchSuggestionItem>();
        if (groups is null)
            return list;

        foreach (var group in groups)
        {
            if (group.Count == 0)
                continue;

            if (!string.IsNullOrWhiteSpace(group.Header))
            {
                list.Add(new SearchSuggestionItem
                {
                    Title = group.Header,
                    Uri = string.Empty,
                    Type = SearchSuggestionType.SectionHeader,
                });
            }

            list.AddRange(group);
        }

        return list;
    }

    public void ShowError(string message)
    {
        HeaderText.Visibility = Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorMessageText.Text = message;
        ResultsScroller.Opacity = 0;
        ResultsScroller.IsHitTestVisible = false;
    }

    // ── Per-item interaction ──────────────────────────────────────────────────

    private void Item_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchSuggestionItem item } && !IsNonSelectable(item.Type))
            ItemClicked?.Invoke(this, item);
    }

    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Panel p && !ReferenceEquals(p, _highlightedElement))
            p.Background = HighlightBrush;
    }

    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Panel p && !ReferenceEquals(p, _highlightedElement))
            p.Background = _transparentBrush;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SearchSuggestionItem item)
            ActionClicked?.Invoke(this, item);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void EntityRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root)
            return;
        if (root.GetValue(EntityDragAttachedProperty) is true)
            return;

        root.SetValue(EntityDragAttachedProperty, true);
        ManualDragAttachment.AttachWithPackageWriter(
            root,
            () => SearchSuggestionInteraction.BuildDragPayload(root.DataContext as SearchSuggestionItem));
    }

    private void EntityRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement root && ShowEntityContextMenu(root, e.GetPosition(root)))
            e.Handled = true;
    }

    private void EntityRoot_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != Microsoft.UI.Input.HoldingState.Started)
            return;

        if (sender is FrameworkElement root && ShowEntityContextMenu(root, e.GetPosition(root)))
            e.Handled = true;
    }

    private bool ShowEntityContextMenu(FrameworkElement root, Point position)
    {
        if (root.DataContext is not SearchSuggestionItem item)
            return false;

        var items = SearchSuggestionInteraction.BuildContextMenu(item);
        if (items.Count == 0)
            return false;

        SuggestionContextMenuOpened?.Invoke(this, EventArgs.Empty);
        var flyout = ContextMenuHost.Show(root, items, position);
        flyout.Closed += (_, _) => SuggestionContextMenuClosed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Render-time album metadata prefetch (Pattern B in AlbumPrefetcher). For every
    // album URI that materialises in a suggestion render, kick off a background
    // ALBUM_V4 fetch; the prefetcher dedups across keystrokes and batches via its
    // 50 ms debounce.
    private void PrefetchAlbumSuggestions(IEnumerable<SearchSuggestionItem>? items)
    {
        if (items is null) return;
        var prefetcher = Ioc.Default.GetService<IAlbumPrefetcher>();
        if (prefetcher is null) return;
        foreach (var item in items)
        {
            if (item.Type == SearchSuggestionType.Album && !string.IsNullOrEmpty(item.Uri))
                prefetcher.EnqueueAlbumPrefetch(item.Uri);
        }
    }

    // ── Static helpers for x:Bind in DataTemplates ──

    public static CornerRadius GetImageCornerRadius(SearchSuggestionType type)
    {
        // Circular for artists (half of the 40px artwork), rounded square otherwise.
        return type == SearchSuggestionType.Artist
            ? new CornerRadius(20)
            : new CornerRadius(4);
    }

    public static Visibility GetActionVisibility(SearchSuggestionType type)
    {
        return type switch
        {
            SearchSuggestionType.Artist => Visibility.Visible,
            SearchSuggestionType.Track => Visibility.Visible,
            SearchSuggestionType.Album => Visibility.Visible,
            SearchSuggestionType.Playlist => Visibility.Visible,
            _ => Visibility.Collapsed
        };
    }

    /// <summary>x:Bind helper for the section-header template. Collapsed for empty
    /// headers so a headerless group renders seamlessly.</summary>
    public static Visibility GetGroupHeaderVisibility(string? header)
        => string.IsNullOrWhiteSpace(header) ? Visibility.Collapsed : Visibility.Visible;

    public static string GetActionGlyph(SearchSuggestionType type)
    {
        return type switch
        {
            SearchSuggestionType.Artist => FluentGlyphs.HeartOutline, // follow
            SearchSuggestionType.Track => FluentGlyphs.Add,           // add to queue
            _ => FluentGlyphs.Add,                                    // save album / playlist
        };
    }

    public static string GetActionTooltip(SearchSuggestionType type)
    {
        return type switch
        {
            SearchSuggestionType.Artist   => "Follow",
            SearchSuggestionType.Track    => "Add to queue",
            SearchSuggestionType.Album    => "Save to library",
            SearchSuggestionType.Playlist => "Save to library",
            _                             => ""
        };
    }
}
