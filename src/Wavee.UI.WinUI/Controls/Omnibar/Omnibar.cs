using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Omnibar;

/// <summary>
/// A search bar control with a custom flyout panel for search suggestions.
/// </summary>
public sealed partial class Omnibar : Control
{
    private const int LostFocusDelayMs = 250;

    private AutoSuggestBox? _searchBox;
    private TextBox? _searchTextBox;
    private Popup? _popup;
    private SearchFlyoutPanel? _flyoutPanel;
    private bool _hasFocus;

    /// <summary>
    /// When true, the suggestions flyout is suppressed (e.g., when already on SearchPage).
    /// Text changes still fire events but the popup won't open.
    /// </summary>
    public bool SuppressFlyout { get; set; }

    public Omnibar()
    {
        DefaultStyleKey = typeof(Omnibar);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Unhook from old template parts before switching to new ones
        if (_searchBox != null)
        {
            _searchBox.TextChanged -= SearchBox_TextChanged;
            _searchBox.QuerySubmitted -= SearchBox_QuerySubmitted;
            _searchBox.GotFocus -= SearchBox_GotFocus;
            _searchBox.LostFocus -= SearchBox_LostFocus;
        }
        if (_searchTextBox != null)
        {
            _searchTextBox.PreviewKeyDown -= TextBox_PreviewKeyDown;
            _searchTextBox = null;
        }
        if (_flyoutPanel != null)
        {
            _flyoutPanel.ItemClicked -= FlyoutPanel_ItemClicked;
            _flyoutPanel.ActionClicked -= FlyoutPanel_ActionClicked;
            _flyoutPanel.RetryRequested -= FlyoutPanel_RetryRequested;
        }

        _searchBox = GetTemplateChild("PART_SearchBox") as AutoSuggestBox;

        if (_searchBox != null)
        {
            _searchBox.TextChanged += SearchBox_TextChanged;
            _searchBox.QuerySubmitted += SearchBox_QuerySubmitted;
            _searchBox.GotFocus += SearchBox_GotFocus;
            _searchBox.LostFocus += SearchBox_LostFocus;
            _searchBox.Loaded += SearchBox_Loaded;
        }

        // Create the custom popup
        _flyoutPanel = new SearchFlyoutPanel();
        _flyoutPanel.ItemClicked += FlyoutPanel_ItemClicked;
        _flyoutPanel.ActionClicked += FlyoutPanel_ActionClicked;
        _flyoutPanel.RetryRequested += FlyoutPanel_RetryRequested;

        _popup = new Popup
        {
            Child = _flyoutPanel,
            IsLightDismissEnabled = false, // We manage dismiss ourselves — light dismiss steals focus from the search box
            ShouldConstrainToRootBounds = false
        };
    }

    private void SearchBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (_searchBox == null) return;
        _searchBox.Loaded -= SearchBox_Loaded;

        _searchTextBox = FindChild<TextBox>(_searchBox);
        if (_searchTextBox != null)
        {
            if (Application.Current.Resources.TryGetValue("OmnibarTextBoxStyle", out var style))
                _searchTextBox.Style = (Style)style;

            // Intercept arrow keys before AutoSuggestBox handles them
            _searchTextBox.PreviewKeyDown += TextBox_PreviewKeyDown;
        }
    }

    private void TextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_popup == null || !_popup.IsOpen || _flyoutPanel == null) return;

        switch (e.Key)
        {
            case VirtualKey.Down:
                _flyoutPanel.MoveSelection(+1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                _flyoutPanel.MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                var selected = _flyoutPanel.GetSelectedItem();
                if (selected != null)
                {
                    HidePopup();
                    SuggestionChosen?.Invoke(this, new OmnibarSuggestionChosenEventArgs(selected));
                    e.Handled = true;
                }
                // If nothing selected, let the AutoSuggestBox handle Enter → QuerySubmitted
                break;
            case VirtualKey.Escape:
                HidePopup();
                e.Handled = true;
                break;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // ── Event handlers ──

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = true;

        if (!TryShowCurrentState())
        {
            // Show shimmer flyout immediately while data loads
            var isRecent = string.IsNullOrWhiteSpace(_searchBox?.Text);
            _flyoutPanel?.ShowShimmer(isRecent);
            ShowPopup();
        }

        // Trigger TextChanged to start the API call
        TextChanged?.Invoke(this, new OmnibarTextChangedEventArgs(_searchBox?.Text ?? ""));
    }

    private async void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = false;

        // Small delay to allow click on popup items to register before closing.
        // If the user clicked a flyout item, FlyoutPanel_ItemClicked will fire
        // within this window and close the popup itself.
        await System.Threading.Tasks.Task.Delay(LostFocusDelayMs);

        // Only close if focus didn't return to the search box (e.g., user clicked
        // a flyout item which programmatically refocuses, or tabbed back)
        if (!_hasFocus)
        {
            HidePopup();
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            if (!TryShowCurrentState())
            {
                // Show shimmer immediately while debounce + API call runs
                var isRecent = string.IsNullOrWhiteSpace(sender.Text);
                _flyoutPanel?.ShowShimmer(isRecent);
                ShowPopup();
            }

            TextChanged?.Invoke(this, new OmnibarTextChangedEventArgs(sender.Text));
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        HidePopup();
        QuerySubmitted?.Invoke(this, new OmnibarQuerySubmittedEventArgs(args.QueryText, args.ChosenSuggestion));
    }

    private void FlyoutPanel_ItemClicked(object? sender, SearchSuggestionItem item)
    {
        HidePopup();
        SuggestionChosen?.Invoke(this, new OmnibarSuggestionChosenEventArgs(item));
    }

    private void FlyoutPanel_ActionClicked(object? sender, SearchSuggestionItem item)
    {
        // Don't close popup - user might want to queue multiple tracks
        ActionButtonClicked?.Invoke(this, item);
    }

    private void FlyoutPanel_RetryRequested(object? sender, EventArgs e)
    {
        if (_searchBox != null)
        {
            _flyoutPanel?.ShowShimmer(string.IsNullOrWhiteSpace(_searchBox.Text));
            ShowPopup();
        }

        RetryRequested?.Invoke(this, new RoutedEventArgs());
    }

    // ── Popup management ──

    private void ShowPopup()
    {
        if (SuppressFlyout) return;
        if (_popup == null || _flyoutPanel == null || _searchBox == null) return;

        // XamlRoot is required for unparented popups (created in code-behind)
        if (_popup.XamlRoot == null)
            _popup.XamlRoot = this.XamlRoot;

        // Inherit theme from the app — Popup children don't get it automatically
        _flyoutPanel.RequestedTheme = this.ActualTheme;

        // Compute popup width:
        //   - at least 480 px (or the input width if it's wider)
        //   - up to 760 px so a 2-column grid (item width 340) fits comfortably
        //   - clamped to available window width minus right padding
        // On narrow windows the popup matches input width and the grid renders 1-column;
        // on wide windows it widens to fit 2 columns of section results.
        const double MinPopupWidth = 480;
        const double MaxPopupWidth = 760;
        const double RightPadding = 16;

        var inputWidth = _searchBox.ActualWidth;
        var transform = _searchBox.TransformToVisual(null);
        var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

        var desiredWidth = Math.Min(MaxPopupWidth, Math.Max(inputWidth, MinPopupWidth));
        var windowWidth = _popup.XamlRoot?.Size.Width ?? inputWidth;
        var availableWidth = Math.Max(inputWidth, windowWidth - point.X - RightPadding);
        var popupWidth = Math.Min(desiredWidth, availableWidth);

        _flyoutPanel.Width = popupWidth;
        _flyoutPanel.MaxWidth = popupWidth;

        _popup.HorizontalOffset = point.X;
        _popup.VerticalOffset = point.Y + _searchBox.ActualHeight + 4;

        if (!_popup.IsOpen)
            _popup.IsOpen = true;
    }

    private void HidePopup()
    {
        if (_popup != null && _popup.IsOpen)
            _popup.IsOpen = false;
    }

    private void UpdateFlyoutState()
    {
        if (_flyoutPanel == null || _searchBox == null) return;

        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            _flyoutPanel.ShowError(ErrorMessage);

            if (_hasFocus)
                ShowPopup();
            return;
        }

        var currentQuery = _searchBox.Text ?? "";

        // Grouped sections take priority: when the VM populated SuggestionGroups
        // for the current query, render the three-section grid. Groups carrying a
        // different QueryText (stale from a prior keystroke) are skipped so we fall
        // through to the shimmer/loading path.
        if (SuggestionGroups is IReadOnlyList<SearchSuggestionGroup> groups
            && HasAnyItems(groups)
            && DoGroupsMatchQuery(groups, currentQuery))
        {
            _flyoutPanel.SetGroups(groups, currentQuery);

            if (_hasFocus)
                ShowPopup();
            return;
        }

        if (SearchResults is List<SearchSuggestionItem> items && items.Count > 0)
        {
            if (!DoResultsMatchQuery(items, currentQuery))
                return;

            var isRecent = string.IsNullOrWhiteSpace(currentQuery);
            _flyoutPanel.SetItems(items, currentQuery, isRecent);

            if (_hasFocus)
                ShowPopup();
            return;
        }

        if (IsLoading)
        {
            _flyoutPanel.ShowShimmer(string.IsNullOrWhiteSpace(currentQuery));

            if (_hasFocus)
                ShowPopup();
            return;
        }

        HidePopup();
    }

    private static bool HasAnyItems(IReadOnlyList<SearchSuggestionGroup> groups)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Count > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Check whether the groups' items were built for the current query text. Returns false
    /// (treat as stale) when the first non-header item's <c>QueryText</c> doesn't match,
    /// so the flyout falls back to shimmer rather than rendering an outdated grid.
    /// </summary>
    private static bool DoGroupsMatchQuery(IReadOnlyList<SearchSuggestionGroup> groups, string queryText)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            var grp = groups[g];
            for (var i = 0; i < grp.Count; i++)
            {
                var item = grp[i];
                if (string.IsNullOrWhiteSpace(queryText))
                    return string.IsNullOrWhiteSpace(item.QueryText);
                return string.Equals(item.QueryText, queryText, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private bool TryShowCurrentState()
    {
        if (_flyoutPanel == null || _searchBox == null) return false;

        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            _flyoutPanel.ShowError(ErrorMessage);
            ShowPopup();
            return true;
        }

        // Prefer grouped mode if any sections are populated for the current query.
        var currentQt = _searchBox.Text ?? string.Empty;
        if (SuggestionGroups is IReadOnlyList<SearchSuggestionGroup> groups
            && HasAnyItems(groups)
            && DoGroupsMatchQuery(groups, currentQt))
        {
            _flyoutPanel.SetGroups(groups, currentQt);
            ShowPopup();
            return true;
        }

        if (SearchResults is not List<SearchSuggestionItem> items || items.Count == 0)
        {
            if (IsLoading)
            {
                _flyoutPanel.ShowShimmer(string.IsNullOrWhiteSpace(_searchBox.Text));
                ShowPopup();
                return true;
            }

            return false;
        }

        var queryText = _searchBox.Text ?? string.Empty;
        if (!DoResultsMatchQuery(items, queryText))
            return false;

        var isRecent = string.IsNullOrWhiteSpace(queryText);
        _flyoutPanel.SetItems(items, queryText, isRecent);
        ShowPopup();
        return true;
    }

    private static bool DoResultsMatchQuery(IReadOnlyList<SearchSuggestionItem> items, string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return items.All(item => string.IsNullOrWhiteSpace(item.QueryText));

        return items.All(item =>
            string.Equals(item.QueryText, queryText, StringComparison.OrdinalIgnoreCase));
    }

    #region Dependency Properties

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Omnibar),
            new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(Omnibar),
            new PropertyMetadata("Search"));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    // Keep for backwards compat but no longer used by built-in dropdown
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(Omnibar),
            new PropertyMetadata(null));

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SearchResultsProperty =
        DependencyProperty.Register(nameof(SearchResults), typeof(object), typeof(Omnibar),
            new PropertyMetadata(null, OnSearchResultsChanged));

    public object? SearchResults
    {
        get => GetValue(SearchResultsProperty);
        set => SetValue(SearchResultsProperty, value);
    }

    private static void OnSearchResultsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Omnibar omnibar)
        {
            omnibar.UpdateFlyoutState();
        }
    }

    // Sectioned suggestions for the three-section omnibar mode (Settings + Your library
    // + Spotify). When this is set with non-empty groups, the flyout renders the grouped
    // grid layout. When null or empty, the flyout falls back to the flat SearchResults
    // path (used for recent searches and the no-match TextQuery row).
    public static readonly DependencyProperty SuggestionGroupsProperty =
        DependencyProperty.Register(nameof(SuggestionGroups), typeof(object), typeof(Omnibar),
            new PropertyMetadata(null, OnSuggestionGroupsChanged));

    public object? SuggestionGroups
    {
        get => GetValue(SuggestionGroupsProperty);
        set => SetValue(SuggestionGroupsProperty, value);
    }

    private static void OnSuggestionGroupsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Omnibar omnibar)
        {
            omnibar.UpdateFlyoutState();
        }
    }

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(Omnibar),
            new PropertyMetadata(false, OnFlyoutStateChanged));

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly DependencyProperty ErrorMessageProperty =
        DependencyProperty.Register(nameof(ErrorMessage), typeof(string), typeof(Omnibar),
            new PropertyMetadata(null, OnFlyoutStateChanged));

    public string? ErrorMessage
    {
        get => (string?)GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    private static void OnFlyoutStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Omnibar omnibar)
        {
            omnibar.UpdateFlyoutState();
        }
    }

    #endregion

    #region Events

    public event TypedEventHandler<Omnibar, OmnibarTextChangedEventArgs>? TextChanged;
    public event TypedEventHandler<Omnibar, OmnibarQuerySubmittedEventArgs>? QuerySubmitted;
    public event TypedEventHandler<Omnibar, OmnibarSuggestionChosenEventArgs>? SuggestionChosen;
    public event TypedEventHandler<Omnibar, SearchSuggestionItem>? ActionButtonClicked;
    public event TypedEventHandler<Omnibar, RoutedEventArgs>? RetryRequested;

    #endregion
}

public class OmnibarTextChangedEventArgs(string text)
{
    public string Text { get; } = text;
}

public class OmnibarQuerySubmittedEventArgs(string queryText, object? chosenSuggestion)
{
    public string QueryText { get; } = queryText;
    public object? ChosenSuggestion { get; } = chosenSuggestion;
}

public class OmnibarSuggestionChosenEventArgs(object? selectedItem)
{
    public object? SelectedItem { get; } = selectedItem;
}
