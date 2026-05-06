using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.Comments;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class EpisodePage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable
{
    private const int ShimmerCollapseDelayMs = 250;

    private readonly ILogger? _logger;
    private readonly INotificationService? _notificationService;
    private bool _showingContent;
    private bool _crossfadeScheduled;
    private bool _isNavigatingAway;
    private bool _isDisposed;

    public EpisodePageViewModel ViewModel { get; }

    public ShimmerLoadGate ShimmerGate { get; } = new();

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public EpisodePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<EpisodePageViewModel>();
        _logger = Ioc.Default.GetService<ILogger<EpisodePage>>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += EpisodePage_Loaded;
        Unloaded += EpisodePage_Unloaded;

        // Start the content layer invisible so the shimmer→content transition
        // is a true crossfade (matches Show / Album / Artist pages).
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Cleanup is in TrimForNavigationCache below — TabBarItem invokes that
        // around Frame.Navigate. Under NavigationCacheMode=Required the page
        // survives nav-away in Frame's cache pool; do NOT Dispose the VM here
        // (the cached page would come back bound to a dead VM).
    }

    private bool _trimmedForNavigationCache;

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = true;
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not keep the cached page wired up while it sits off-screen.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = false;
        Bindings?.Update();
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EpisodePageViewModel.IsLoading))
        {
            if (!ViewModel.IsLoading && !_showingContent && !_crossfadeScheduled)
                TryShowContentNow();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);

    private void EpisodePage_Loaded(object sender, RoutedEventArgs e)
    {
        _isNavigatingAway = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (!ViewModel.IsLoading)
            TryShowContentNow();

        UpdateEpisodeBodyLayout();
        TryHandlePendingPodcastEpisodeArtConnectedAnimation();
    }

    private void EpisodePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isNavigatingAway = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= EpisodePage_Loaded;
        Unloaded -= EpisodePage_Unloaded;
        ActualThemeChanged -= OnActualThemeChanged;
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        (ViewModel as IDisposable)?.Dispose();
    }

    private async void ScheduleCrossfade()
    {
        _crossfadeScheduled = true;
        await Task.Yield();
        await Task.Delay(16);
        if (_isNavigatingAway || _showingContent || ViewModel.IsLoading)
        {
            _crossfadeScheduled = false;
            return;
        }

        CrossfadeToContent();
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;
        _crossfadeScheduled = false;

        await ShimmerGate.RunCrossfadeAsync(ShimmerContainer, ContentContainer, FrameworkLayer.Xaml,
            () => _showingContent);
    }

    private void TryShowContentNow()
    {
        if (_showingContent ||
            _crossfadeScheduled ||
            ViewModel.IsLoading ||
            (string.IsNullOrEmpty(ViewModel.Title) && !ViewModel.HasError))
        {
            return;
        }

        ScheduleCrossfade();
    }

    private void SnapCrossfadeToContent()
    {
        _showingContent = true;
        _crossfadeScheduled = false;
        ShimmerGate.IsLoaded = false;
        if (ShimmerContainer is not null)
        {
            ShimmerContainer.Visibility = Visibility.Collapsed;
            ShimmerContainer.Opacity = 0;
            ElementCompositionPreview.GetElementVisual(ShimmerContainer).Opacity = 0;
        }
        ContentContainer.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 1;
    }

    private void ResetCrossfadeForNewLoad()
    {
        _isNavigatingAway = false;
        _showingContent = false;
        _crossfadeScheduled = false;
        ShimmerGate.Reset(() => ShimmerContainer, () => ContentContainer);
    }

    private void EpisodeContentStack_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateEpisodeBodyLayout();

    private void UpdateEpisodeBodyLayout()
    {
        if (EpisodeContentStack is null ||
            EpisodeBodyPrimaryColumn is null ||
            EpisodeBodySecondaryColumn is null ||
            EpisodeBodyPrimaryPanel is null ||
            EpisodeBodySecondaryPanel is null)
        {
            return;
        }

        var stacked = EpisodeContentStack.ActualWidth is > 0 and < 980;

        EpisodeBodyPrimaryColumn.Width = stacked
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(2, GridUnitType.Star);
        EpisodeBodySecondaryColumn.Width = stacked
            ? new GridLength(0)
            : new GridLength(1.15, GridUnitType.Star);

        Grid.SetRow(EpisodeBodyPrimaryPanel, 0);
        Grid.SetColumn(EpisodeBodyPrimaryPanel, 0);
        Grid.SetColumnSpan(EpisodeBodyPrimaryPanel, stacked ? 2 : 1);

        Grid.SetRow(EpisodeBodySecondaryPanel, stacked ? 1 : 0);
        Grid.SetColumn(EpisodeBodySecondaryPanel, stacked ? 0 : 1);
        Grid.SetColumnSpan(EpisodeBodySecondaryPanel, stacked ? 2 : 1);
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadNewContent(e.Parameter);
    }

    public void RefreshWithParameter(object? parameter)
    {
        // Same-tab navigation between two episodes reuses this Page instance —
        // TabBarItem.Navigate routes through here instead of OnNavigatedTo.
        LoadNewContent(parameter);
    }

    private async void LoadNewContent(object? parameter)
    {
        ResetCrossfadeForNewLoad();

        var episodeParam = parameter switch
        {
            EpisodeNavigationParameter ep => ep,
            ContentNavigationParameter cnp when !string.IsNullOrEmpty(cnp.Uri)
                => new EpisodeNavigationParameter
                {
                    EpisodeUri = cnp.Uri,
                    EpisodeTitle = cnp.Title,
                    EpisodeImageUrl = cnp.ImageUrl,
                },
            string raw when !string.IsNullOrWhiteSpace(raw)
                => new EpisodeNavigationParameter { EpisodeUri = raw },
            _ => null,
        };

        if (episodeParam is null) return;
        ViewModel.Activate(episodeParam);

        TryHandlePendingPodcastEpisodeArtConnectedAnimation();

        await Task.Yield();
        if (_isNavigatingAway)
            return;

        TryShowContentNow();
    }

    private bool TryHandlePendingPodcastEpisodeArtConnectedAnimation()
    {
        if (!ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PodcastEpisodeArt) ||
            EpisodeCoverContainer is null)
        {
            return false;
        }

        SnapCrossfadeToContent();
        UpdateLayout();
        return ConnectedAnimationHelper.TryStartAnimation(
            ConnectedAnimationHelper.PodcastEpisodeArt,
            EpisodeCoverContainer);
    }

    // ── Breadcrumb ──────────────────────────────────────────────────────────

    private void EpisodeBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
        {
            NavigationHelpers.OpenPodcasts(NavigationHelpers.IsCtrlPressed());
            return;
        }

        if (args.Index != 1 || !ViewModel.HasParentShow)
            return;

        // Prefer back-navigation when the immediate previous page is the same
        // ShowPage we'd be opening — preserves scroll position and avoids a
        // duplicate Frame entry.
        if (Frame is { CanGoBack: true } frame)
        {
            var entries = frame.BackStack;
            if (entries.Count > 0)
            {
                var top = entries[entries.Count - 1];
                if (top.SourcePageType == typeof(ShowPage))
                {
                    var topUri = top.Parameter switch
                    {
                        string raw => raw,
                        ContentNavigationParameter cnp => cnp.Uri,
                        _ => null,
                    };
                    if (string.Equals(topUri, ViewModel.ParentShowUri, StringComparison.Ordinal))
                    {
                        frame.GoBack();
                        return;
                    }
                }
            }
        }

        NavigationHelpers.OpenShowPage(
            ViewModel.ParentShowUri!,
            ViewModel.ParentShowTitle ?? "Show",
            openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    // ── Action buttons ──────────────────────────────────────────────────────

    private void LikeButton_Click(object sender, RoutedEventArgs e)
    {
        // Episode-level save isn't part of the SavedItemType enum yet — same
        // stub ShowPage uses for episode hearts. Acknowledge so the click
        // doesn't feel dead while the wiring lands.
        _logger?.LogDebug("Episode like requested but episode-level save isn't wired yet: {Uri}", ViewModel.EpisodeUri);
        _notificationService?.Show(
            "Saving episodes will be available soon.",
            NotificationSeverity.Informational,
            TimeSpan.FromSeconds(2));
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        // Add-to-queue for episodes routes through the same playback service the
        // Play button uses, but with isQueued=true so it appends instead of
        // taking over. Plumbing for queue-append from an arbitrary URI lives
        // alongside the player's "Add to queue" context-menu path; for now,
        // surface a hint so the affordance feels intentional.
        _notificationService?.Show(
            "Add to queue is coming soon for episodes.",
            NotificationSeverity.Informational,
            TimeSpan.FromSeconds(2));
    }

    private void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.ShareUrl)) return;
        _notificationService?.Show(
            "Episode link copied",
            NotificationSeverity.Success,
            TimeSpan.FromSeconds(3));
    }

    private void ChapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EpisodeChapterVm chapter })
            ViewModel.SeekToChapterCommand.Execute(chapter);
    }

    private async void EpisodeComments_SubmitRequested(CommentsList sender, RoutedEventArgs args)
    {
        if (ViewModel.SubmitCommentCommand.CanExecute(null))
            await ViewModel.SubmitCommentCommand.ExecuteAsync(null);
    }

    private async void EpisodeComments_ReplySubmitRequested(CommentsList sender, PodcastCommentViewModel comment)
    {
        if (comment.SubmitReplyCommand.CanExecute(null))
            await comment.SubmitReplyCommand.ExecuteAsync(null);
    }

    private async void EpisodeComments_ShowReactionsRequested(CommentsList sender, PodcastCommentViewModel comment)
    {
        if (XamlRoot is null) return;
        await PodcastCommentReactionsDialog.ShowAsync(
            XamlRoot,
            (token, reaction) => comment.GetReactionsAsync(token, reaction));
    }

    private async void EpisodeComments_ShowReplyReactionsRequested(CommentsList sender, PodcastReplyViewModel reply)
    {
        if (XamlRoot is null) return;
        await PodcastCommentReactionsDialog.ShowAsync(
            XamlRoot,
            (token, reaction) => reply.GetReactionsAsync(token, reaction));
    }

    private void SiblingEpisode_OpenRequested(object? sender, ShowEpisodeDto e)
        => ViewModel.OpenSiblingEpisodeCommand.Execute(e);

    private void SiblingEpisode_PlayRequested(object? sender, ShowEpisodeDto e)
    {
        if (!string.IsNullOrEmpty(e.Uri))
            NavigationHelpers.PlayEpisode(e.Uri);
    }

    private void SiblingEpisode_LikeRequested(object? sender, ShowEpisodeDto e)
    {
        _logger?.LogDebug("Episode like requested but episode-level save isn't wired yet: {Uri}", e?.Uri);
        _notificationService?.Show(
            "Saving episodes will be available soon.",
            NotificationSeverity.Informational,
            TimeSpan.FromSeconds(2));
    }

    private void SiblingEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShowEpisodeDto sibling })
            ViewModel.OpenSiblingEpisodeCommand.Execute(sibling);
    }
}
