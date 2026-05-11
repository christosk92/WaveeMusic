using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Omnibar;

public sealed partial class SearchFlyoutPanel : UserControl
{
    private string _queryText = "";
    private int _keyboardIndex = -1; // -1 = nothing selected via keyboard

    // CollectionViewSource drives the grouped grid mode. Items panel inside each
    // group is an ItemsWrapGrid (defined in XAML) — adaptive 1/2-column based on
    // the popup width set by Omnibar.ShowPopup.
    private readonly CollectionViewSource _groupedSource = new() { IsSourceGrouped = true };
    private bool _isGroupedMode;

    public event EventHandler<SearchSuggestionItem>? ItemClicked;
    public event EventHandler<SearchSuggestionItem>? ActionClicked;
    public event EventHandler? RetryRequested;

    public SearchFlyoutPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Moves the keyboard highlight up or down. Returns the currently highlighted item (or null).
    /// Does NOT change the search box text.
    /// </summary>
    public SearchSuggestionItem? MoveSelection(int delta)
    {
        if (ResultsList.Items == null || ResultsList.Items.Count == 0)
            return null;

        var count = ResultsList.Items.Count;
        var step = delta >= 0 ? 1 : -1;

        // Step in the requested direction, skipping non-selectable rows (shimmer
        // placeholders, defensive SectionHeader rows). Bail out if we wrap past
        // the boundary in either direction — matches the previous "deselect on
        // overflow" behavior.
        var candidate = _keyboardIndex + (delta == 0 ? 0 : step);
        while (true)
        {
            if (candidate < -1) { candidate = -1; break; }
            if (candidate >= count) { candidate = -1; break; }
            if (candidate == -1) break;
            if (ResultsList.Items[candidate] is SearchSuggestionItem item
                && !IsNonSelectable(item.Type))
                break;
            candidate += step;
        }

        _keyboardIndex = candidate;
        ResultsList.SelectedIndex = _keyboardIndex;

        if (_keyboardIndex >= 0)
        {
            ResultsList.ScrollIntoView(ResultsList.Items[_keyboardIndex]);
            return ResultsList.Items[_keyboardIndex] as SearchSuggestionItem;
        }

        return null;
    }

    private static bool IsNonSelectable(SearchSuggestionType type)
        => type == SearchSuggestionType.Shimmer
        || type == SearchSuggestionType.SectionHeader;

    /// <summary>
    /// Gets the currently keyboard-selected item, or null if none.
    /// </summary>
    public SearchSuggestionItem? GetSelectedItem()
    {
        if (_keyboardIndex >= 0 && _keyboardIndex < (ResultsList.Items?.Count ?? 0))
            return ResultsList.Items![_keyboardIndex] as SearchSuggestionItem;
        return null;
    }

    /// <summary>
    /// Resets keyboard selection (e.g., when new results arrive).
    /// </summary>
    public void ResetSelection()
    {
        _keyboardIndex = -1;
        ResultsList.SelectedIndex = -1;
    }

    /// <summary>
    /// Shows the shimmer loading state immediately while data is being fetched.
    /// </summary>
    /// <remarks>
    /// This method is idempotent — if the shimmer is already visible we only update the
    /// header and return. Critically, we fade the <see cref="ResultsList"/> via Opacity
    /// rather than collapsing it, so its ListViewItem containers stay realized and can
    /// be recycled by the next <see cref="SetItems"/> call. The previous behaviour
    /// (<c>ResultsList.Visibility = Collapsed</c>) tore down every container on every
    /// keystroke, causing visible UI hangs while the user typed.
    /// </remarks>
    public void ShowShimmer(bool isRecentSearches)
    {
        // Fast path — already showing shimmer. Only the header label may need updating
        // (e.g. "Recent searches" vs. hidden when the user starts typing).
        if (ShimmerPanel.Visibility == Visibility.Visible)
        {
            HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        // Fade (don't collapse) so containers survive. Next SetItems will restore opacity.
        ResultsList.Opacity = 0;
        ResultsList.IsHitTestVisible = false;
    }

    /// <summary>
    /// Swaps from shimmer to real data. Restores opacity so previously-realized
    /// ListView containers become visible again, and replaces the items source —
    /// the ListView recycles existing containers in place rather than rebuilding.
    /// Flat-list path: used for the legacy "Recent searches" mode and the no-match
    /// fallback when there's nothing across the three sectioned groups.
    /// </summary>
    public void SetItems(List<SearchSuggestionItem>? items, string queryText, bool isRecentSearches)
    {
        _queryText = queryText;
        _isGroupedMode = false;
        HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsList.Opacity = 1;
        ResultsList.IsHitTestVisible = true;
        ResultsList.Visibility = Visibility.Visible;
        ResultsList.ItemsSource = items;
        ResetSelection();
    }

    /// <summary>
    /// Grouped path used by the three-section omnibar mode (Settings / Your library /
    /// Spotify). Each <see cref="SearchSuggestionGroup"/> renders as a section header
    /// above a wrapping grid of items. Groups with zero items are skipped automatically
    /// by <c>GroupStyle.HidesIfEmpty</c>.
    /// </summary>
    public void SetGroups(IReadOnlyList<SearchSuggestionGroup>? groups, string queryText)
    {
        _queryText = queryText;
        _isGroupedMode = true;
        HeaderText.Visibility = Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsList.Opacity = 1;
        ResultsList.IsHitTestVisible = true;
        ResultsList.Visibility = Visibility.Visible;

        if (groups is null || groups.Count == 0)
        {
            // Nothing to show — clear out.
            _groupedSource.Source = null;
            ResultsList.ItemsSource = null;
        }
        else
        {
            _groupedSource.Source = groups;
            if (!ReferenceEquals(ResultsList.ItemsSource, _groupedSource.View))
                ResultsList.ItemsSource = _groupedSource.View;
        }

        ResetSelection();
    }

    public void ShowError(string message)
    {
        HeaderText.Visibility = Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorMessageText.Text = message;
        ResultsList.Opacity = 0;
        ResultsList.IsHitTestVisible = false;
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestionItem item && !IsNonSelectable(item.Type))
            ItemClicked?.Invoke(this, item);
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

    // Called after the ListView renders items to apply bold matching on text suggestions
    public void ApplyBoldMatching()
    {
        if (string.IsNullOrEmpty(_queryText)) return;

        foreach (var container in ResultsList.Items)
        {
            if (container is SearchSuggestionItem { Type: SearchSuggestionType.TextQuery } item)
            {
                var listItem = ResultsList.ContainerFromItem(item) as ListViewItem;
                var rtb = FindChild<RichTextBlock>(listItem, "SuggestionText");
                if (rtb != null)
                {
                    ApplyBoldPrefix(rtb, item.Title, _queryText);
                }
            }
        }
    }

    private static void ApplyBoldPrefix(RichTextBlock rtb, string text, string query)
    {
        rtb.Blocks.Clear();
        var paragraph = new Paragraph();

        var matchIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (matchIndex >= 0)
        {
            // Before match
            if (matchIndex > 0)
                paragraph.Inlines.Add(new Run { Text = text[..matchIndex] });

            // Bold match
            paragraph.Inlines.Add(new Run
            {
                Text = text.Substring(matchIndex, query.Length),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });

            // After match
            var afterIndex = matchIndex + query.Length;
            if (afterIndex < text.Length)
                paragraph.Inlines.Add(new Run { Text = text[afterIndex..] });
        }
        else
        {
            paragraph.Inlines.Add(new Run { Text = text });
        }

        rtb.Blocks.Add(paragraph);
    }

    // ── Static helpers for x:Bind in DataTemplates ──

    public static CornerRadius GetImageCornerRadius(SearchSuggestionType type)
    {
        // Circular for artists, rounded square for everything else
        return type == SearchSuggestionType.Artist
            ? new CornerRadius(24)
            : new CornerRadius(4);
    }

    public static Visibility GetActionVisibility(SearchSuggestionType type)
    {
        // Spotify entity types only — local entities (LocalTrack/Album/Artist/Playlist)
        // and Setting/TextQuery/SectionHeader fall through to Collapsed.
        return type switch
        {
            SearchSuggestionType.Artist => Visibility.Visible,
            SearchSuggestionType.Track => Visibility.Visible,
            SearchSuggestionType.Album => Visibility.Visible,
            SearchSuggestionType.Playlist => Visibility.Visible,
            _ => Visibility.Collapsed
        };
    }

    /// <summary>x:Bind helper for the group-header DataTemplate. Returns Collapsed for
    /// empty/whitespace headers so groups can render seamlessly (e.g. the legacy
    /// recent-searches one-group payload).</summary>
    public static Visibility GetGroupHeaderVisibility(string? header)
        => string.IsNullOrWhiteSpace(header) ? Visibility.Collapsed : Visibility.Visible;

    public static string GetActionGlyph(SearchSuggestionType type)
    {
        return type switch
        {
            SearchSuggestionType.Artist => "\uEB51",  // Heart outline (follow)
            SearchSuggestionType.Track  => "\uE710",  // Add (plus)
            _                           => "\uE710",  // Add (plus) for albums/playlists
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

    private static T? FindChild<T>(DependencyObject? parent, string name) where T : FrameworkElement
    {
        if (parent == null) return null;

        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var result = FindChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
