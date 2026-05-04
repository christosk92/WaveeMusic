using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Controls.Library;

/// <summary>
/// Library Sort by + View as control. Drop-down button that shows the active sort key,
/// direction, and view mode. The flyout uses a Fluent-style vertical list where the active
/// row shows a green check and a direction chevron (click it to toggle asc/desc); clicking
/// another row selects it.
/// </summary>
public sealed partial class LibrarySortViewPanel : UserControl
{
    private bool _suppressCallbacks;

    public static readonly DependencyProperty SortByProperty =
        DependencyProperty.Register(
            nameof(SortBy),
            typeof(LibrarySortBy),
            typeof(LibrarySortViewPanel),
            new PropertyMetadata(LibrarySortBy.Recents, OnSortByChanged));

    public static readonly DependencyProperty SortDirectionProperty =
        DependencyProperty.Register(
            nameof(SortDirection),
            typeof(LibrarySortDirection),
            typeof(LibrarySortViewPanel),
            new PropertyMetadata(LibrarySortDirection.Descending, OnSortDirectionChanged));

    public static readonly DependencyProperty ViewModeProperty =
        DependencyProperty.Register(
            nameof(ViewMode),
            typeof(LibraryViewMode),
            typeof(LibrarySortViewPanel),
            new PropertyMetadata(LibraryViewMode.DefaultGrid, OnViewModeChanged));

    public static readonly DependencyProperty AllowedSortKeysProperty =
        DependencyProperty.Register(
            nameof(AllowedSortKeys),
            typeof(string),
            typeof(LibrarySortViewPanel),
            new PropertyMetadata(
                "Recents,RecentlyAdded,Alphabetical,Creator,ReleaseDate",
                OnAllowedSortKeysChanged));

    public static readonly DependencyProperty GridScaleProperty =
        DependencyProperty.Register(
            nameof(GridScale),
            typeof(double),
            typeof(LibrarySortViewPanel),
            new PropertyMetadata(1.0));

    public static readonly DependencyProperty ShowGridScaleProperty =
        DependencyProperty.Register(
            nameof(ShowGridScale),
            typeof(bool),
            typeof(LibrarySortViewPanel),
            new PropertyMetadata(false, OnShowGridScaleChanged));

    public LibrarySortBy SortBy
    {
        get => (LibrarySortBy)GetValue(SortByProperty);
        set => SetValue(SortByProperty, value);
    }

    public LibrarySortDirection SortDirection
    {
        get => (LibrarySortDirection)GetValue(SortDirectionProperty);
        set => SetValue(SortDirectionProperty, value);
    }

    public LibraryViewMode ViewMode
    {
        get => (LibraryViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    /// <summary>
    /// Comma-separated list of <see cref="LibrarySortBy"/> keys that are offered as
    /// rows. Rows whose key is not in this list are hidden. Used to trim Creator /
    /// ReleaseDate on the Artists tab.
    /// </summary>
    public string AllowedSortKeys
    {
        get => (string)GetValue(AllowedSortKeysProperty);
        set => SetValue(AllowedSortKeysProperty, value);
    }

    public double GridScale
    {
        get => (double)GetValue(GridScaleProperty);
        set => SetValue(GridScaleProperty, value);
    }

    public bool ShowGridScale
    {
        get => (bool)GetValue(ShowGridScaleProperty);
        set => SetValue(ShowGridScaleProperty, value);
    }

    public LibrarySortViewPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAllowedSortKeys();
        ApplySortByToUi();
        ApplyDirectionToUi();
        ApplyViewModeToUi();
        ApplyGridScaleVisibility();
        UpdateTriggerDisplay();
    }

    // ── DP change handlers ──

    private static void OnSortByChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (LibrarySortViewPanel)d;
        panel.ApplySortByToUi();
        panel.UpdateTriggerDisplay();
    }

    private static void OnSortDirectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (LibrarySortViewPanel)d;
        panel.ApplyDirectionToUi();
        panel.UpdateTriggerDisplay();
    }

    private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (LibrarySortViewPanel)d;
        panel.ApplyViewModeToUi();
        panel.ApplyGridScaleVisibility();
        panel.UpdateTriggerDisplay();
    }

    private static void OnAllowedSortKeysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LibrarySortViewPanel)d).ApplyAllowedSortKeys();
    }

    private static void OnShowGridScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LibrarySortViewPanel)d).ApplyGridScaleVisibility();
    }

    private void ApplyGridScaleVisibility()
    {
        if (GridScaleSection == null) return;
        var isGridMode = ViewMode is LibraryViewMode.DefaultGrid or LibraryViewMode.CompactGrid;
        GridScaleSection.Visibility = (ShowGridScale && isGridMode) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── UI sync ──

    private HashSet<LibrarySortBy> GetAllowedKeys()
    {
        var allowed = new HashSet<LibrarySortBy>();
        var raw = AllowedSortKeys ?? string.Empty;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<LibrarySortBy>(part.Trim(), ignoreCase: true, out var key))
                allowed.Add(key);
        }

        if (allowed.Count == 0)
            allowed = new HashSet<LibrarySortBy>((LibrarySortBy[])Enum.GetValues(typeof(LibrarySortBy)));

        return allowed;
    }

    private readonly record struct SortRow(LibrarySortBy Key, Button Row, FontIcon Check, FontIcon Direction);

    private IEnumerable<SortRow> EnumerateSortRows()
    {
        if (SortRecents == null) yield break;
        yield return new SortRow(LibrarySortBy.Recents, SortRecents, SortRecentsCheck, SortRecentsDir);
        yield return new SortRow(LibrarySortBy.RecentlyAdded, SortRecentlyAdded, SortRecentlyAddedCheck, SortRecentlyAddedDir);
        yield return new SortRow(LibrarySortBy.Alphabetical, SortAlphabetical, SortAlphabeticalCheck, SortAlphabeticalDir);
        yield return new SortRow(LibrarySortBy.Creator, SortCreator, SortCreatorCheck, SortCreatorDir);
        yield return new SortRow(LibrarySortBy.ReleaseDate, SortReleaseDate, SortReleaseDateCheck, SortReleaseDateDir);
    }

    private void ApplyAllowedSortKeys()
    {
        var allowed = GetAllowedKeys();
        foreach (var row in EnumerateSortRows())
        {
            row.Row.Visibility = allowed.Contains(row.Key) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!allowed.Contains(SortBy))
            SortBy = allowed.Contains(LibrarySortBy.Recents) ? LibrarySortBy.Recents : allowed.First();
    }

    private ToggleButton? GetToggleForViewMode(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.CompactList => ViewCompactList,
        LibraryViewMode.DefaultList => ViewDefaultList,
        LibraryViewMode.CompactGrid => ViewCompactGrid,
        LibraryViewMode.DefaultGrid => ViewDefaultGrid,
        _ => null
    };

    private void ApplySortByToUi()
    {
        foreach (var row in EnumerateSortRows())
        {
            var isActive = row.Key == SortBy;
            row.Check.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            row.Direction.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyDirectionToUi()
    {
        var glyph = SortDirection == LibrarySortDirection.Ascending ? "\uE74A" : "\uE74B";
        foreach (var row in EnumerateSortRows())
        {
            row.Direction.Glyph = glyph;
        }
    }

    private void ApplyViewModeToUi()
    {
        if (ViewDefaultGrid == null) return;

        _suppressCallbacks = true;
        try
        {
            var target = GetToggleForViewMode(ViewMode);
            foreach (var tb in new[] { ViewCompactList, ViewDefaultList, ViewCompactGrid, ViewDefaultGrid })
            {
                if (tb is null) continue;
                var shouldCheck = ReferenceEquals(tb, target);
                if (tb.IsChecked != shouldCheck)
                    tb.IsChecked = shouldCheck;
            }
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    private void UpdateTriggerDisplay()
    {
        if (TriggerSortLabel == null) return;

        TriggerSortLabel.Text = SortBy switch
        {
            LibrarySortBy.Recents => "Recents",
            LibrarySortBy.RecentlyAdded => "Recently added",
            LibrarySortBy.Alphabetical => "Alphabetical",
            LibrarySortBy.Creator => "Creator",
            LibrarySortBy.ReleaseDate => "Release date",
            _ => "Recents"
        };

        if (TriggerDirectionGlyph != null)
            TriggerDirectionGlyph.Glyph = SortDirection == LibrarySortDirection.Ascending ? "\uE74A" : "\uE74B";

        if (TriggerViewGlyph != null)
            TriggerViewGlyph.Glyph = ViewMode switch
            {
                LibraryViewMode.CompactList => "\uE8FD",
                LibraryViewMode.DefaultList => "\uE14C",
                LibraryViewMode.CompactGrid => "\uE80A",
                LibraryViewMode.DefaultGrid => "\uF0E2",
                _ => "\uF0E2"
            };
    }

    // ── User-initiated events ──

    private void SortOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!Enum.TryParse<LibrarySortBy>(tag, ignoreCase: true, out var requested)) return;

        if (requested == SortBy)
        {
            // Clicking the active row flips direction (Spotify-style behavior).
            SortDirection = SortDirection == LibrarySortDirection.Ascending
                ? LibrarySortDirection.Descending
                : LibrarySortDirection.Ascending;
        }
        else
        {
            SortBy = requested;
        }
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;

        if (sender is ToggleButton tb)
        {
            // Radio group behavior: re-click keeps checked.
            if (tb.IsChecked != true)
            {
                tb.IsChecked = true;
                return;
            }

            LibraryViewMode? mode = null;
            if (ReferenceEquals(tb, ViewCompactList)) mode = LibraryViewMode.CompactList;
            else if (ReferenceEquals(tb, ViewDefaultList)) mode = LibraryViewMode.DefaultList;
            else if (ReferenceEquals(tb, ViewCompactGrid)) mode = LibraryViewMode.CompactGrid;
            else if (ReferenceEquals(tb, ViewDefaultGrid)) mode = LibraryViewMode.DefaultGrid;

            if (mode.HasValue)
                ViewMode = mode.Value;

            _suppressCallbacks = true;
            try
            {
                foreach (var other in new[] { ViewCompactList, ViewDefaultList, ViewCompactGrid, ViewDefaultGrid })
                {
                    if (other is null || ReferenceEquals(other, tb)) continue;
                    if (other.IsChecked == true)
                        other.IsChecked = false;
                }
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }
    }
}
