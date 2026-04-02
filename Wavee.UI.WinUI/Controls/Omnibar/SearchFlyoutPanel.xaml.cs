using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Omnibar;

public sealed partial class SearchFlyoutPanel : UserControl
{
    private string _queryText = "";
    private int _keyboardIndex = -1; // -1 = nothing selected via keyboard

    public event EventHandler<SearchSuggestionItem>? ItemClicked;
    public event EventHandler<SearchSuggestionItem>? ActionClicked;

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
        _keyboardIndex += delta;

        // Wrap: going above first → deselect; going below last → deselect
        if (_keyboardIndex < -1) _keyboardIndex = count - 1;
        if (_keyboardIndex >= count) _keyboardIndex = -1;

        ResultsList.SelectedIndex = _keyboardIndex;

        // Scroll into view
        if (_keyboardIndex >= 0)
        {
            ResultsList.ScrollIntoView(ResultsList.Items[_keyboardIndex]);
            return ResultsList.Items[_keyboardIndex] as SearchSuggestionItem;
        }

        return null;
    }

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
    public void ShowShimmer(bool isRecentSearches)
    {
        HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Swaps from shimmer to real data.
    /// </summary>
    public void SetItems(List<SearchSuggestionItem>? items, string queryText, bool isRecentSearches)
    {
        _queryText = queryText;
        HeaderText.Visibility = isRecentSearches ? Visibility.Visible : Visibility.Collapsed;
        ShimmerPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;
        ResultsList.ItemsSource = items;
        ResetSelection();
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestionItem item)
            ItemClicked?.Invoke(this, item);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SearchSuggestionItem item)
            ActionClicked?.Invoke(this, item);
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
        return type switch
        {
            SearchSuggestionType.Artist => Visibility.Visible,
            SearchSuggestionType.Track => Visibility.Visible,
            SearchSuggestionType.Album => Visibility.Visible,
            SearchSuggestionType.Playlist => Visibility.Visible,
            _ => Visibility.Collapsed
        };
    }

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
