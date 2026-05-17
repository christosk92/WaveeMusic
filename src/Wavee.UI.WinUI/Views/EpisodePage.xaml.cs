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

public sealed partial class EpisodePage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable, IContentPageHost
{
    private readonly ILogger? _logger;
    private readonly INotificationService? _notificationService;
    private bool _isDisposed;

    public EpisodePageViewModel ViewModel { get; }

    public ContentPageController PageController { get; }

    public ShimmerLoadGate ShimmerGate => PageController.ShimmerGate;

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── IContentPageHost ─────────────────────────────────────────────────────
    FrameworkElement? IContentPageHost.ShimmerContainer => ShimmerContainer;
    FrameworkElement IContentPageHost.ContentContainer => ContentContainer;
    FrameworkLayer IContentPageHost.CrossfadeLayer => FrameworkLayer.Xaml;
    string IContentPageHost.PageIdForLogging => $"episode:{ViewModel.EpisodeUri ?? "?"}";
    bool IContentPageHost.IsLoading => ViewModel.IsLoading;
    bool IContentPageHost.HasContent => !string.IsNullOrEmpty(ViewModel.Title) || ViewModel.HasError;

    public EpisodePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<EpisodePageViewModel>();
        _logger = Ioc.Default.GetService<ILogger<EpisodePage>>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        PageController = new ContentPageController(this, _logger);
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
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.episode.onNavigatedFrom");
        base.OnNavigatedFrom(e);
        // Eager trim on every nav-away so the VM's singleton subscriptions
        // release immediately. Without this, the cached EpisodePage's VM
        // stays rooted by PlaybackStateService.PropertyChanged across Frame
        // evictions and produces creeping 1–2 s click delays over time.
        TrimForNavigationCache();
    }

    private bool _trimmedForNavigationCache;

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = true;
        // Drop the singleton subscriptions that pin this VM across cached-
        // page evictions. Activate re-attaches on nav-back.
        ViewModel.Hibernate();
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not keep the cached page wired up while it sits off-screen.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = false;
        // Defer Bindings.Update to the next dispatcher tick so DWM gets a
        // paint frame between the page reattaching and the synchronous
        // binding sweep. Frame.Navigate runs this method sync, so without
        // the defer the user sees the page pop in fully rendered before the
        // PageEntranceFade ever animates.
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.episode.bindingsUpdate"))
            {
                Bindings?.Update();
            }
        });
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EpisodePageViewModel.IsLoading))
            PageController.OnIsLoadingChanged();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);

    private void EpisodePage_Loaded(object sender, RoutedEventArgs e)
    {
        PageController.IsNavigatingAway = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (!ViewModel.IsLoading)
            PageController.TryShowContentNow();

        UpdateEpisodeBodyLayout();
        TryHandlePendingPodcastEpisodeArtConnectedAnimation();
    }

    private void EpisodePage_Unloaded(object sender, RoutedEventArgs e)
    {
        PageController.IsNavigatingAway = true;
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
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.episode.onNavigatedTo");
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
        PageController.ResetForNewLoad();

        // Yield once between the shimmer flip and the synchronous Activate
        // chain. Without this, OnNavigatedTo runs the whole sequence in one
        // UI-thread tick — DWM never gets a paint frame to show the just-
        // armed shimmer OR the page-entrance fade's first percent.
        await Task.Yield();
        if (PageController.IsNavigatingAway)
            return;

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
        if (PageController.IsNavigatingAway)
            return;

        PageController.TryShowContentNow();
    }

    private bool TryHandlePendingPodcastEpisodeArtConnectedAnimation()
    {
        if (!ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PodcastEpisodeArt) ||
            EpisodeCoverContainer is null)
        {
            return false;
        }

        PageController.MarkContentShownDirectly();
        using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.episode.updateLayout"))
        {
            UpdateLayout();
        }
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
