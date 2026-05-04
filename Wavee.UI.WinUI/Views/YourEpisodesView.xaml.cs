using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.TrackDataGrid;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class YourEpisodesView : UserControl, IDisposable
{
    private const double NarrowLayoutBreakpoint = 980;
    private const double WideLayoutSplitterTotalWidth = 24;
    private const double DefaultShowsColumnWidth = 260;
    private const double DefaultEpisodesColumnWidth = 520;
    private const double MinShowsColumnWidth = 220;
    private const double MaxShowsColumnWidth = 340;
    private const double MinEpisodesColumnWidth = 320;
    private const double MinDetailsColumnWidth = 280;

    private readonly List<TrackDataGrid> _episodeGroupGrids = [];
    private bool _hasInitializedLayoutMode;
    private bool _clearingEpisodeGridSelection;
    private bool _wideColumnsInitialized;
    private bool _applyingWideColumnWidths;
    private bool _disposed;

    public YourEpisodesViewModel ViewModel { get; }

    public YourEpisodesView(YourEpisodesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: false);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: _hasInitializedLayoutMode);
    }

    private void UpdateLayoutMode(bool preserveContext)
    {
        var isNarrow = ActualWidth <= NarrowLayoutBreakpoint;
        ViewModel.SetNarrowLayout(isNarrow, preserveContext);
        _hasInitializedLayoutMode = true;
    }

    private async void WideLayoutRoot_Loaded(object sender, RoutedEventArgs e)
    {
        await System.Threading.Tasks.Task.Yield();
        if (_disposed || WideLayoutRoot.ActualWidth <= 0)
            return;

        var saved = ViewModel.GetPodcastWideColumnWidths();
        var shows = saved?.Shows ?? (PodcastShowsColumn.ActualWidth > 0
            ? PodcastShowsColumn.ActualWidth
            : DefaultShowsColumnWidth);
        var episodes = saved?.Episodes ?? (PodcastEpisodesColumn.ActualWidth > 0
            ? PodcastEpisodesColumn.ActualWidth
            : DefaultEpisodesColumnWidth);

        ApplyWideColumnWidths(shows, episodes);
        _wideColumnsInitialized = true;
    }

    private void WideLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_wideColumnsInitialized || _applyingWideColumnWidths || e.NewSize.Width <= 0)
            return;

        ApplyWideColumnWidths(PodcastShowsColumn.ActualWidth, PodcastEpisodesColumn.ActualWidth);
    }

    private void WideColumnSplitter_ResizeCompleted(object? sender, GridSplitterResizeCompletedEventArgs e)
    {
        if (!_wideColumnsInitialized || _applyingWideColumnWidths)
            return;

        ApplyWideColumnWidths(PodcastShowsColumn.ActualWidth, PodcastEpisodesColumn.ActualWidth);
        ViewModel.SavePodcastWideColumnWidths(
            PodcastShowsColumn.ActualWidth,
            PodcastEpisodesColumn.ActualWidth,
            PodcastDetailsColumn.ActualWidth);
    }

    private void ApplyWideColumnWidths(double shows, double episodes)
    {
        if (WideLayoutRoot.ActualWidth <= 0)
            return;

        var available = Math.Max(0, WideLayoutRoot.ActualWidth - WideLayoutSplitterTotalWidth);
        if (available <= 0)
            return;

        var showsWidth = Math.Clamp(shows, MinShowsColumnWidth, MaxShowsColumnWidth);
        var episodesWidth = Math.Max(MinEpisodesColumnWidth, episodes);

        var maxEpisodes = Math.Max(MinEpisodesColumnWidth, available - showsWidth - MinDetailsColumnWidth);
        episodesWidth = Math.Min(episodesWidth, maxEpisodes);

        var maxShows = Math.Max(MinShowsColumnWidth, available - episodesWidth - MinDetailsColumnWidth);
        showsWidth = Math.Min(showsWidth, Math.Min(MaxShowsColumnWidth, maxShows));

        _applyingWideColumnWidths = true;
        try
        {
            PodcastShowsColumn.Width = new GridLength(showsWidth, GridUnitType.Pixel);
            PodcastEpisodesColumn.Width = new GridLength(episodesWidth, GridUnitType.Pixel);
            PodcastDetailsColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        finally
        {
            _applyingWideColumnWidths = false;
        }
    }

    private void EpisodeGroupGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TrackDataGrid grid)
            return;

        if (!_episodeGroupGrids.Contains(grid))
            _episodeGroupGrids.Add(grid);
        grid.RowSelected -= EpisodeGroupGrid_RowSelected;
        grid.RowSelected += EpisodeGroupGrid_RowSelected;

        grid.DateAddedFormatter = item =>
            item is LibraryEpisodeDto episode ? episode.AddedAtFormatted : "";
    }

    private void EpisodeGroupGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TrackDataGrid grid)
            return;

        grid.RowSelected -= EpisodeGroupGrid_RowSelected;
        _episodeGroupGrids.Remove(grid);
    }

    private void EpisodeGroupGrid_RowSelected(object? sender, Wavee.UI.WinUI.Data.Contracts.ITrackItem selected)
    {
        if (_clearingEpisodeGridSelection)
            return;

        _clearingEpisodeGridSelection = true;
        try
        {
            foreach (var grid in _episodeGroupGrids.ToArray())
            {
                if (!ReferenceEquals(grid, sender))
                    grid.ClearSelection();
            }
        }
        finally
        {
            _clearingEpisodeGridSelection = false;
        }
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
            return;

        var flyout = new MenuFlyout();
        AddSortItem(flyout, "Recently added", LibrarySortBy.RecentlyAdded);
        AddSortItem(flyout, "Alphabetical", LibrarySortBy.Alphabetical);
        AddSortItem(flyout, "Publisher", LibrarySortBy.Creator);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var directionItem = new MenuFlyoutItem
        {
            Text = ViewModel.SortDirection == LibrarySortDirection.Ascending
                ? "Descending"
                : "Ascending"
        };
        directionItem.Click += (_, _) => ViewModel.ToggleSortDirectionCommand.Execute(null);
        flyout.Items.Add(directionItem);

        flyout.ShowAt(target);
    }

    private void OpenSelectedEpisodeDetails_Click(object sender, RoutedEventArgs e)
    {
        PrepareConnectedAnimationNear(sender as DependencyObject, "EpisodeDetailCoverContainer", ConnectedAnimationHelper.PodcastEpisodeArt);
        if (ViewModel.OpenSelectedEpisodeDetailsCommand.CanExecute(null))
            ViewModel.OpenSelectedEpisodeDetailsCommand.Execute(null);
    }

    private void OpenSelectedShowDetails_Click(object sender, RoutedEventArgs e)
    {
        PrepareConnectedAnimationNear(sender as DependencyObject, "SelectedShowCoverContainer", ConnectedAnimationHelper.PodcastArt);
        if (ViewModel.OpenSelectedShowCommand.CanExecute(null))
            ViewModel.OpenSelectedShowCommand.Execute(null);
    }

    private void PodcastEpisodeScopeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not Segmented segmented)
            return;

        ViewModel.SetPodcastEpisodeScope(
            segmented.SelectedIndex == 1
                ? PodcastEpisodeScope.Latest
                : PodcastEpisodeScope.Saved);
    }

    private void AddSortItem(MenuFlyout flyout, string text, LibrarySortBy sortBy)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "PodcastSortBy",
            IsChecked = ViewModel.SortBy == sortBy,
            Tag = sortBy
        };
        item.Click += (_, _) => ViewModel.SetSortBy(sortBy);
        flyout.Items.Add(item);
    }

    private void NarrowShowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: LibraryPodcastShowDto show })
        {
            ViewModel.ShowSelectedShowEpisodes(show);
        }
    }

    private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            ViewModel.ShowPodcastsRoot();
        else if (args.Index == 1)
            ViewModel.ShowSelectedShowEpisodes();
    }

    private void BrowsePodcasts_Click(object sender, RoutedEventArgs e)
    {
        NavigationHelpers.OpenPodcastBrowse(NavigationHelpers.IsCtrlPressed());
    }

    private static void PrepareConnectedAnimationNear(DependencyObject? origin, string sourceName, string key)
    {
        var source = FindNearestNamedDescendant<FrameworkElement>(origin, sourceName);
        if (source is null)
            return;

        ConnectedAnimationHelper.PrepareAnimation(key, source);
    }

    private static T? FindNearestNamedDescendant<T>(DependencyObject? origin, string name)
        where T : FrameworkElement
    {
        var current = origin;
        while (current is not null)
        {
            var match = FindDescendantByName<T>(current, name);
            if (match is not null)
                return match;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendantByName<T>(DependencyObject? root, string name)
        where T : FrameworkElement
    {
        if (root is null)
            return null;

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
                return element;

            var descendant = FindDescendantByName<T>(child, name);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
    }
}
