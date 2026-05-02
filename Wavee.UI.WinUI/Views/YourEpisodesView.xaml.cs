using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.TrackDataGrid;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class YourEpisodesView : UserControl, IDisposable
{
    private const double NarrowLayoutBreakpoint = 980;
    private const double EpisodeDetailShyHeaderFallbackPinOffset = 84;
    private const double WideLayoutSplitterTotalWidth = 24;
    private const double DefaultShowsColumnWidth = 300;
    private const double DefaultEpisodesColumnWidth = 420;
    private const double MinShowsColumnWidth = 220;
    private const double MaxShowsColumnWidth = 420;
    private const double MinEpisodesColumnWidth = 320;
    private const double MinDetailsColumnWidth = 300;
    private readonly List<TrackDataGrid> _episodeGroupGrids = [];
    private readonly List<HtmlTextBlock> _episodeAboutBlocks = [];
    private readonly List<HyperlinkButton> _episodeAboutShowMoreButtons = [];
    private readonly Dictionary<ScrollView, EpisodeDetailShyHeaderState> _episodeDetailShyHeaders = [];
    private readonly Dictionary<FrameworkElement, EpisodeDetailBodyState> _episodeDetailBodyStates = [];
    private bool _hasInitializedLayoutMode;
    private bool _commentConsentDialogOpen;
    private bool _clearingEpisodeGridSelection;
    private bool _episodeAboutExpanded;
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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

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
        await Task.Yield();
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
        {
            ViewModel.ShowPodcastsRoot();
        }
        else if (args.Index == 1)
        {
            ViewModel.ShowSelectedShowEpisodes();
        }
    }

    private void EpisodeAboutBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not HtmlTextBlock block) return;
        if (!_episodeAboutBlocks.Contains(block))
            _episodeAboutBlocks.Add(block);
        block.MaxLines = _episodeAboutExpanded ? 0 : 4;
    }

    private void EpisodeAboutBlock_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is HtmlTextBlock block)
            _episodeAboutBlocks.Remove(block);
    }

    private void EpisodeAboutShowMoreButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton button) return;
        if (!_episodeAboutShowMoreButtons.Contains(button))
            _episodeAboutShowMoreButtons.Add(button);
        button.Content = _episodeAboutExpanded ? "Show less" : "Show more";
    }

    private void EpisodeAboutShowMoreButton_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button)
            _episodeAboutShowMoreButtons.Remove(button);
    }

    private void EpisodeAboutShowMore_Click(object sender, RoutedEventArgs e)
    {
        _episodeAboutExpanded = !_episodeAboutExpanded;
        ApplyEpisodeAboutExpandedState();
    }

    private void ApplyEpisodeAboutExpandedState()
    {
        var maxLines = _episodeAboutExpanded ? 0 : 4;
        var label = _episodeAboutExpanded ? "Show less" : "Show more";
        foreach (var block in _episodeAboutBlocks)
            block.MaxLines = maxLines;
        foreach (var button in _episodeAboutShowMoreButtons)
            button.Content = label;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(YourEpisodesViewModel.SelectedEpisode))
        {
            ResetEpisodeDetailScrollState(scrollToTop: true);
            SnapEpisodeDetailBodiesToCurrentState();
        }

        if (e.PropertyName == nameof(YourEpisodesViewModel.IsEpisodeDetailLoading))
        {
            _ = UpdateEpisodeDetailBodiesAsync(animated: !ViewModel.IsEpisodeDetailLoading);
        }

        if (e.PropertyName != nameof(YourEpisodesViewModel.SelectedEpisodeDescription)) return;
        if (!_episodeAboutExpanded) return;

        _episodeAboutExpanded = false;
        ApplyEpisodeAboutExpandedState();
    }

    private void EpisodeDetailBodyHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host)
            return;

        var loadedContent = FindDescendantByName<FrameworkElement>(host, "EpisodeDetailLoadedContent");
        var loadingSkeleton = FindDescendantByName<FrameworkElement>(host, "EpisodeDetailLoadingSkeleton");
        if (loadedContent is null || loadingSkeleton is null)
            return;

        var state = new EpisodeDetailBodyState(host, loadedContent, loadingSkeleton);
        _episodeDetailBodyStates[host] = state;
        SnapEpisodeDetailBodyToCurrentState(state);
    }

    private void EpisodeDetailBodyHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host)
            _episodeDetailBodyStates.Remove(host);
    }

    private void SnapEpisodeDetailBodiesToCurrentState()
    {
        foreach (var state in new List<EpisodeDetailBodyState>(_episodeDetailBodyStates.Values))
            SnapEpisodeDetailBodyToCurrentState(state);
    }

    private void SnapEpisodeDetailBodyToCurrentState(EpisodeDetailBodyState state)
    {
        state.Generation++;
        state.Host.MinHeight = 0;

        if (ViewModel.ShowEpisodeDetailLoadingSkeleton)
        {
            state.LoadedContent.Opacity = 0;
            state.LoadedContent.Visibility = Visibility.Visible;
            state.LoadedContent.IsHitTestVisible = false;
            state.LoadingSkeleton.Visibility = Visibility.Visible;
            state.LoadingSkeleton.Opacity = 1;
            return;
        }

        state.LoadingSkeleton.Opacity = 0;
        state.LoadingSkeleton.Visibility = Visibility.Collapsed;
        if (ViewModel.HasSelectedEpisode)
        {
            state.LoadedContent.Visibility = Visibility.Visible;
            state.LoadedContent.Opacity = 1;
            state.LoadedContent.IsHitTestVisible = true;
        }
        else
        {
            state.LoadedContent.Opacity = 0;
            state.LoadedContent.Visibility = Visibility.Collapsed;
            state.LoadedContent.IsHitTestVisible = false;
        }
    }

    private async Task UpdateEpisodeDetailBodiesAsync(bool animated)
    {
        var tasks = new List<Task>();
        foreach (var state in new List<EpisodeDetailBodyState>(_episodeDetailBodyStates.Values))
            tasks.Add(UpdateEpisodeDetailBodyAsync(state, animated));

        await Task.WhenAll(tasks);
    }

    private async Task UpdateEpisodeDetailBodyAsync(EpisodeDetailBodyState state, bool animated)
    {
        state.Generation++;
        var generation = state.Generation;

        if (ViewModel.ShowEpisodeDetailLoadingSkeleton || !animated)
        {
            SnapEpisodeDetailBodyToCurrentState(state);
            return;
        }

        if (!ViewModel.HasSelectedEpisode)
        {
            SnapEpisodeDetailBodyToCurrentState(state);
            return;
        }

        var skeletonHeight = state.LoadingSkeleton.Visibility == Visibility.Visible
            ? state.LoadingSkeleton.ActualHeight
            : state.Host.ActualHeight;

        state.Host.MinHeight = Math.Max(0, skeletonHeight);
        state.LoadingSkeleton.Visibility = Visibility.Visible;
        state.LoadedContent.Visibility = Visibility.Visible;
        state.LoadedContent.Opacity = 0;
        state.LoadedContent.IsHitTestVisible = false;

        await Task.Yield();
        await Task.Delay(16);

        if (_disposed || generation != state.Generation)
            return;

        state.Host.MinHeight = Math.Max(skeletonHeight, state.LoadedContent.ActualHeight);
        state.LoadedContent.IsHitTestVisible = true;

        try
        {
            var skeletonFade = AnimationBuilder.Create()
                .Opacity(from: state.LoadingSkeleton.Opacity, to: 0, duration: TimeSpan.FromMilliseconds(180),
                    layer: FrameworkLayer.Xaml)
                .StartAsync(state.LoadingSkeleton);

            var contentFade = AnimationBuilder.Create()
                .Opacity(from: state.LoadedContent.Opacity, to: 1, duration: TimeSpan.FromMilliseconds(260),
                    delay: TimeSpan.FromMilliseconds(80),
                    layer: FrameworkLayer.Xaml)
                .StartAsync(state.LoadedContent);

            await Task.WhenAll(skeletonFade, contentFade);
        }
        catch
        {
            if (_disposed)
                return;
        }

        if (_disposed || generation != state.Generation)
            return;

        state.LoadingSkeleton.Visibility = Visibility.Collapsed;
        state.LoadedContent.Opacity = 1;
        state.LoadedContent.Visibility = Visibility.Visible;
        state.LoadedContent.IsHitTestVisible = true;
        state.Host.MinHeight = 0;
    }

    private void EpisodeDetailScrollView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollView scrollView)
            return;

        var root = scrollView.Parent as DependencyObject;
        var headerCard = FindDescendantByName<FrameworkElement>(root, "EpisodeDetailShyHeaderCard");
        if (headerCard is null)
            return;

        _episodeDetailShyHeaders[scrollView] = new EpisodeDetailShyHeaderState(
            headerCard,
            FindDescendantByName<FrameworkElement>(root, "EpisodeDetailHero"));

        ResetEpisodeDetailShyHeader(scrollView, scrollToTop: false);
        _ = EvaluateEpisodeDetailShyHeaderAsync(scrollView);
    }

    private void EpisodeDetailScrollView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollView scrollView)
            return;

        _episodeDetailShyHeaders.Remove(scrollView);
    }

    private void EpisodeDetailScrollView_ViewChanged(ScrollView sender, object args)
    {
        _ = EvaluateEpisodeDetailShyHeaderAsync(sender);
    }

    private async Task EvaluateEpisodeDetailShyHeaderAsync(ScrollView scrollView)
    {
        if (_disposed || !_episodeDetailShyHeaders.TryGetValue(scrollView, out var state))
            return;

        if (state.IsAnimating)
        {
            state.RecheckPending = true;
            return;
        }

        while (!_disposed)
        {
            var pinOffset = GetEpisodeDetailShyHeaderPinOffset(state);
            var shouldPin = scrollView.Visibility == Visibility.Visible
                            && scrollView.VerticalOffset >= pinOffset;

            if (shouldPin == state.IsPinned)
                return;

            state.IsAnimating = true;
            state.RecheckPending = false;
            var generation = state.Generation;

            try
            {
                if (shouldPin)
                    await ShowEpisodeDetailShyHeaderAsync(state.HeaderCard);
                else
                    await HideEpisodeDetailShyHeaderAsync(state.HeaderCard);

                if (_disposed || state.Generation != generation)
                {
                    CollapseEpisodeDetailShyHeader(state);
                    return;
                }

                state.IsPinned = shouldPin;
            }
            catch
            {
                return;
            }
            finally
            {
                state.IsAnimating = false;
            }

            if (!state.RecheckPending)
                return;
        }
    }

    private static double GetEpisodeDetailShyHeaderPinOffset(EpisodeDetailShyHeaderState state)
    {
        var heroHeight = state.Hero?.ActualHeight ?? 0;
        return heroHeight > 0
            ? Math.Max(56, heroHeight - 24)
            : EpisodeDetailShyHeaderFallbackPinOffset;
    }

    private static async Task ShowEpisodeDetailShyHeaderAsync(FrameworkElement headerCard)
    {
        headerCard.Visibility = Visibility.Visible;

        await AnimationBuilder.Create()
            .Opacity(from: headerCard.Opacity, to: 1, duration: TimeSpan.FromMilliseconds(180))
            .Translation(Axis.Y, from: -8, to: 0, duration: TimeSpan.FromMilliseconds(180))
            .StartAsync(headerCard);
    }

    private static async Task HideEpisodeDetailShyHeaderAsync(FrameworkElement headerCard)
    {
        await AnimationBuilder.Create()
            .Opacity(from: headerCard.Opacity, to: 0, duration: TimeSpan.FromMilliseconds(120))
            .Translation(Axis.Y, to: -8, duration: TimeSpan.FromMilliseconds(120))
            .StartAsync(headerCard);

        headerCard.Visibility = Visibility.Collapsed;
    }

    private void ResetEpisodeDetailScrollState(bool scrollToTop)
    {
        foreach (var scrollView in _episodeDetailShyHeaders.Keys)
            ResetEpisodeDetailShyHeader(scrollView, scrollToTop);
    }

    private void ResetEpisodeDetailShyHeader(ScrollView scrollView, bool scrollToTop)
    {
        if (!_episodeDetailShyHeaders.TryGetValue(scrollView, out var state))
            return;

        state.IsPinned = false;
        state.IsAnimating = false;
        state.RecheckPending = false;
        state.Generation++;
        CollapseEpisodeDetailShyHeader(state);

        if (scrollToTop && scrollView.Visibility == Visibility.Visible)
            scrollView.ScrollToImmediate(0, 0);
    }

    private static void CollapseEpisodeDetailShyHeader(EpisodeDetailShyHeaderState state)
    {
        state.HeaderCard.Opacity = 0;
        state.HeaderCard.Translation = new Vector3(0, -8, 0);
        state.HeaderCard.Visibility = Visibility.Collapsed;
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

    private async void SubmitCommentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.SubmitCommentCommand.CanExecute(null))
            return;

        if (!await EnsurePodcastCommentsConsentAcceptedAsync())
            return;

        if (ViewModel.SubmitCommentCommand.CanExecute(null))
            await ViewModel.SubmitCommentCommand.ExecuteAsync(null);
    }

    private async void SubmitReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PodcastCommentViewModel comment })
            return;

        if (!comment.SubmitReplyCommand.CanExecute(null))
            return;

        if (!await EnsurePodcastCommentsConsentAcceptedAsync())
            return;

        if (comment.SubmitReplyCommand.CanExecute(null))
            await comment.SubmitReplyCommand.ExecuteAsync(null);
    }

    private async void ShowCommentReactionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        switch (element.DataContext)
        {
            case PodcastCommentViewModel comment:
                await ShowPodcastCommentReactionsDialogAsync((token, reaction) =>
                    comment.GetReactionsAsync(token, reaction));
                break;
            case PodcastReplyViewModel reply:
                await ShowPodcastCommentReactionsDialogAsync((token, reaction) =>
                    reply.GetReactionsAsync(token, reaction));
                break;
        }
    }

    private async Task ShowPodcastCommentReactionsDialogAsync(
        Func<string?, string?, Task<PodcastCommentReactionsPageDto?>> loadPageAsync)
    {
        var reactions = new List<PodcastCommentReactionDto>();
        IReadOnlyList<PodcastCommentReactionCountDto> reactionCounts = [];
        string? selectedReaction = null;
        string? nextPageToken = null;

        var chipsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var listPanel = new StackPanel { Spacing = 6 };
        var statusText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = GetBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var loadMoreButton = new Button
        {
            Content = "Load more",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 6, 14, 6),
            Visibility = Visibility.Collapsed
        };

        var content = new StackPanel
        {
            Spacing = 14,
            MinWidth = 420,
            MaxWidth = 540
        };
        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = chipsPanel
        });
        content.Children.Add(statusText);
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel
        });
        content.Children.Add(loadMoreButton);

        async Task LoadAsync(bool reset)
        {
            if (reset)
            {
                reactions.Clear();
                nextPageToken = null;
            }
            else if (string.IsNullOrWhiteSpace(nextPageToken))
            {
                return;
            }

            loadMoreButton.IsEnabled = false;
            statusText.Text = reset ? "Loading reactions..." : "Loading more reactions...";
            statusText.Visibility = Visibility.Visible;

            PodcastCommentReactionsPageDto? page;
            try
            {
                page = await loadPageAsync(reset ? null : nextPageToken, selectedReaction);
            }
            catch
            {
                statusText.Text = "Could not load reactions.";
                statusText.Visibility = Visibility.Visible;
                loadMoreButton.IsEnabled = true;
                RenderChips();
                return;
            }

            if (page is not null)
            {
                reactionCounts = page.ReactionCounts;
                reactions.AddRange(page.Items);
                nextPageToken = page.NextPageToken;
            }

            RenderChips();
            RenderList();
            loadMoreButton.IsEnabled = true;
        }

        void RenderChips()
        {
            chipsPanel.Children.Clear();
            var total = reactionCounts.Sum(static count => count.Count);
            chipsPanel.Children.Add(BuildReactionFilterButton(
                total > 0 ? $"All {total:N0}" : "All",
                selectedReaction is null,
                async () =>
                {
                    selectedReaction = null;
                    await LoadAsync(reset: true);
                }));

            foreach (var count in reactionCounts)
            {
                chipsPanel.Children.Add(BuildReactionFilterButton(
                    $"{count.ReactionUnicode} {count.CountFormatted}",
                    string.Equals(selectedReaction, count.ReactionUnicode, StringComparison.Ordinal),
                    async () =>
                    {
                        selectedReaction = count.ReactionUnicode;
                        await LoadAsync(reset: true);
                    }));
            }
        }

        void RenderList()
        {
            listPanel.Children.Clear();
            statusText.Visibility = Visibility.Collapsed;

            if (reactions.Count == 0)
            {
                statusText.Text = "No reactions yet.";
                statusText.Visibility = Visibility.Visible;
            }
            else
            {
                foreach (var reaction in reactions)
                    listPanel.Children.Add(BuildReactionRow(reaction));
            }

            loadMoreButton.Visibility = string.IsNullOrWhiteSpace(nextPageToken)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        loadMoreButton.Click += async (_, _) => await LoadAsync(reset: false);

        await LoadAsync(reset: true);

        var dialog = new ContentDialog
        {
            Title = "Reactions",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = content,
            XamlRoot = XamlRoot
        };

        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var styleObject) &&
            styleObject is Style style)
        {
            dialog.Style = style;
        }

        await dialog.ShowAsync();
    }

    private static Button BuildReactionFilterButton(string label, bool selected, Func<Task> click)
    {
        var button = new Button
        {
            Content = label,
            MinHeight = 0,
            Padding = new Thickness(12, 6,12,6),
            Background = selected
                ? GetBrush("AccentFillColorDefaultBrush")
                : GetBrush("SubtleFillColorSecondaryBrush"),
            Foreground = selected
                ? GetBrush("TextOnAccentFillColorPrimaryBrush")
                : GetBrush("TextFillColorPrimaryBrush"),
            BorderBrush = GetBrush("CardStrokeColorDefaultBrush")
        };
        button.Click += async (_, _) => await click();
        return button;
    }

    private static FrameworkElement BuildReactionRow(PodcastCommentReactionDto reaction)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(0, 6, 0, 6)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var person = new PersonPicture
        {
            Width = 40,
            Height = 40,
            DisplayName = reaction.AuthorName
        };
        if (!string.IsNullOrWhiteSpace(reaction.AuthorImageUrl) &&
            Uri.TryCreate(reaction.AuthorImageUrl, UriKind.Absolute, out var imageUri))
        {
            person.ProfilePicture = new BitmapImage(imageUri);
        }
        Grid.SetColumn(person, 0);
        grid.Children.Add(person);

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(reaction.AuthorName) ? "Spotify user" : reaction.AuthorName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        });
        text.Children.Add(new TextBlock
        {
            Text = reaction.CreatedAtFormatted,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GetBrush("TextFillColorSecondaryBrush")
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var emoji = new TextBlock
        {
            Text = reaction.ReactionUnicode,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(emoji, 2);
        grid.Children.Add(emoji);

        return grid;
    }

    private async Task<bool> EnsurePodcastCommentsConsentAcceptedAsync()
    {
        if (ViewModel.HasAcceptedPodcastCommentsConsent)
            return true;

        if (_commentConsentDialogOpen)
            return false;

        _commentConsentDialogOpen = true;
        try
        {
            var accepted = await ShowPodcastCommentConsentDialogAsync();
            if (!accepted)
                return false;

            await ViewModel.AcceptPodcastCommentsConsentAsync();
            return true;
        }
        finally
        {
            _commentConsentDialogOpen = false;
        }
    }

    private async Task<bool> ShowPodcastCommentConsentDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Ready to leave a comment on Spotify?",
            PrimaryButtonText = "Let's go",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };
        dialog.Content = BuildPodcastCommentConsentContent(
            allChecked => dialog.IsPrimaryButtonEnabled = allChecked);

        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var styleObject) &&
            styleObject is Style style)
        {
            dialog.Style = style;
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static FrameworkElement BuildPodcastCommentConsentContent(Action<bool> consentStateChanged)
    {
        var stack = new StackPanel
        {
            Spacing = 18,
            MaxWidth = 720
        };

        stack.Children.Add(new TextBlock
        {
            Text = "We want to hear from you. First, a few things to keep in mind.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        });

        var acknowledgements = new List<CheckBox>();
        var bullets = new StackPanel { Spacing = 12 };

        void AddAcknowledgement(string title, string body)
        {
            var checkbox = BuildConsentCheckbox(title, body);
            acknowledgements.Add(checkbox);
            checkbox.Checked += (_, _) => UpdateConsentState();
            checkbox.Unchecked += (_, _) => UpdateConsentState();
            bullets.Children.Add(checkbox);
        }

        void UpdateConsentState()
        {
            foreach (var acknowledgement in acknowledgements)
            {
                if (acknowledgement.IsChecked != true)
                {
                    consentStateChanged(false);
                    return;
                }
            }

            consentStateChanged(acknowledgements.Count > 0);
        }

        AddAcknowledgement(
            "Comments are public",
            "Your comments, name, and profile picture may be visible to others on Spotify if your profile is public.");
        AddAcknowledgement(
            "Some comments need review",
            "Your comment may require additional approval before it is visible to others.");
        AddAcknowledgement(
            "Safety matters most",
            "You can delete your own comments, report harmful ones, and block other accounts.");

        stack.Children.Add(new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(6),
            Background = GetBrush("CardBackgroundFillColorSecondaryBrush"),
            Child = bullets
        });

        stack.Children.Add(BuildPodcastCommentConsentLinksText());

        return stack;
    }

    private static TextBlock BuildPodcastCommentConsentLinksText()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GetBrush("TextFillColorSecondaryBrush")
        };

        text.Inlines.Add(new Run { Text = "For more information, read Spotify's " });
        text.Inlines.Add(BuildExternalTextLink(
            "Platform Rules",
            "https://www.spotify.com/nl/safetyandprivacy/platform-rules"));
        text.Inlines.Add(new Run { Text = " and " });
        text.Inlines.Add(BuildExternalTextLink(
            "Terms of Use",
            "https://www.spotify.com/nl/legal/end-user-agreement/k"));
        text.Inlines.Add(new Run { Text = "." });

        return text;
    }

    private static Hyperlink BuildExternalTextLink(string label, string url)
    {
        var link = new Hyperlink
        {
            NavigateUri = new Uri(url)
        };
        link.Inlines.Add(new Run { Text = label });
        return link;
    }

    private static CheckBox BuildConsentCheckbox(string title, string body)
    {
        var text = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(2, 0, 0, 0)
        };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        });
        text.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = GetBrush("TextFillColorSecondaryBrush")
        });

        return new CheckBox
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6, 4, 0, 4)
        };
    }

    private static Brush GetBrush(string resourceKey)
        => (Brush)Application.Current.Resources[resourceKey];

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _episodeAboutBlocks.Clear();
        _episodeAboutShowMoreButtons.Clear();
        _episodeDetailShyHeaders.Clear();
        _episodeDetailBodyStates.Clear();
    }

    private sealed class EpisodeDetailShyHeaderState(FrameworkElement headerCard, FrameworkElement? hero)
    {
        public FrameworkElement HeaderCard { get; } = headerCard;
        public FrameworkElement? Hero { get; } = hero;
        public bool IsPinned { get; set; }
        public bool IsAnimating { get; set; }
        public bool RecheckPending { get; set; }
        public int Generation { get; set; }
    }

    private sealed class EpisodeDetailBodyState(
        FrameworkElement host,
        FrameworkElement loadedContent,
        FrameworkElement loadingSkeleton)
    {
        public FrameworkElement Host { get; } = host;
        public FrameworkElement LoadedContent { get; } = loadedContent;
        public FrameworkElement LoadingSkeleton { get; } = loadingSkeleton;
        public int Generation { get; set; }
    }
}
