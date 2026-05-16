using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using AppNotificationSeverity = Wavee.UI.WinUI.Data.Models.NotificationSeverity;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Views;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Docking;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core;
using Wavee.Core.Playlists;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IThemeService _themeService;
    private readonly INotificationService _notificationService;
    private readonly ISearchService _searchService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly AppModel _appModel;
    private readonly IShellSessionService _shellSession;
    private readonly ISettingsService? _settingsService;
    private IPanelDockingService? _docking;
    private readonly ILogger? _logger;
    private readonly IDispatcherService? _dispatcher;
    private readonly PlaylistMosaicService? _mosaicService;
    private readonly Helpers.Debouncer _searchDebouncer = new(TimeSpan.FromMilliseconds(300));
    private readonly Dictionary<string, CachedSearchSuggestions> _querySuggestionCache = new(StringComparer.OrdinalIgnoreCase);
    private CachedSearchSuggestions? _recentSearchesCache;
    private string _activeSearchText = string.Empty;

    // Omnibar grouped-search backend. Quicksearch is broadened to AllCached so anything
    // the user has seen/played (not just saved-library items) is findable.
    private readonly Wavee.Local.ILocalLibraryService? _localLibrary;

    // Resolves Spotify URL / URI pastes to a single entity preview shown in the omnibar.
    // Optional — when null, link pastes still navigate but show only the synthetic
    // "Open {kind}" placeholder card (no real name / cover).
    private readonly ISpotifyLinkPreviewService? _linkPreviewService;

    // CTS scoped to the currently-typed URL paste, so a fresh keystroke cancels any
    // in-flight preview fetch before the result lands on a stale query.
    private CancellationTokenSource? _linkPreviewCts;

    // Placeholder items used as the Spotify section's contents while the network call
    // is in flight. The Spotify section becomes visible from frame 1 (shimmer rows),
    // then the real items replace these when RefreshQuerySuggestionsAsync resolves.
    // 4 entries — matches a 2×2 grid at wide widths, 4 stacked rows at narrow widths.
    private static readonly IReadOnlyList<SearchSuggestionItem> SpotifyShimmerPlaceholders =
    [
        new() { Title = string.Empty, Uri = "wavee:shimmer:0", Type = SearchSuggestionType.Shimmer },
        new() { Title = string.Empty, Uri = "wavee:shimmer:1", Type = SearchSuggestionType.Shimmer },
        new() { Title = string.Empty, Uri = "wavee:shimmer:2", Type = SearchSuggestionType.Shimmer },
        new() { Title = string.Empty, Uri = "wavee:shimmer:3", Type = SearchSuggestionType.Shimmer },
    ];
    private bool _restoringTabSession;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tabSleepTimer;
    private DateTimeOffset _lastTabSleepMemoryReleaseUtc = DateTimeOffset.MinValue;

    private const int MaxCachedSuggestionQueries = 24;
    private static readonly TimeSpan RecentSearchesCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuerySuggestionsCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TabSleepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TabSleepEvaluationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TabSleepMemoryReleaseThrottle = TimeSpan.FromSeconds(45);

    // UI element references for cleanup
    private Microsoft.UI.Xaml.Controls.Button? _playlistsAddButton;
    private Microsoft.UI.Xaml.Controls.MenuFlyoutItem? _newPlaylistMenuItem;
    private Microsoft.UI.Xaml.Controls.MenuFlyoutItem? _newFolderMenuItem;

    // Static collection accessible from NavigationHelpers
    public static ObservableCollection<TabBarItem> TabInstances { get; } = [];

    /// <summary>
    /// Select a tab by index - updates both SelectedTabIndex and SelectedTabItem
    /// </summary>
    public void SelectTab(int index)
    {
        if (index >= 0 && index < TabInstances.Count)
        {
            SelectedTabIndex = index;
            SelectedTabItem = TabInstances[index];
        }
    }

    // Instance property for XAML binding
    public ObservableCollection<TabBarItem> Tabs => TabInstances;

    [ObservableProperty]
    private TabBarItem? _selectedTabItem;

    [ObservableProperty]
    private int _selectedTabIndex;

    // Track previous tab index for animation direction
    private int _previousTabIndex;

    // Direction for tab switch animation (1 = right, -1 = left, 0 = none)
    [ObservableProperty]
    private int _tabSwitchDirection;

    [ObservableProperty]
    private double _sidebarWidth = 280;

    [ObservableProperty]
    private SidebarDisplayMode _sidebarDisplayMode = SidebarDisplayMode.Expanded;

    [ObservableProperty]
    private bool _isSidebarPaneOpen;

    [ObservableProperty]
    private double _rightPanelWidth = 300;

    [ObservableProperty]
    private bool _isRightPanelOpen;

    [ObservableProperty]
    private RightPanelMode _rightPanelMode = RightPanelMode.Queue;

    [ObservableProperty]
    private PlayerLocation _playerLocation = PlayerLocation.Bottom;

    /// <summary>
    /// Single source of truth for tear-off state. Bound from XAML (visibility
    /// gates) via <see cref="IsRightPanelVisibleInShell"/> /
    /// <see cref="IsSidebarPlayerVisibleInShell"/>. Lazily resolved so existing
    /// constructor wiring stays untouched.
    /// </summary>
    public IPanelDockingService Docking =>
        _docking ??= ResolveAndSubscribeDocking();

    private IPanelDockingService ResolveAndSubscribeDocking()
    {
        var svc = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<IPanelDockingService>();
        svc.PropertyChanged += OnDockingPropertyChanged;
        return svc;
    }

    private void OnDockingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPanelDockingService.IsRightPanelDetached))
        {
            OnPropertyChanged(nameof(IsRightPanelVisibleInShell));
        }
        else if (e.PropertyName == nameof(IPanelDockingService.IsPlayerDetached))
        {
            if (!Docking.IsPlayerDetached
                && PlayerLocation == PlayerLocation.Sidebar
                && SidebarDisplayMode != SidebarDisplayMode.Expanded)
            {
                PlayerLocation = PlayerLocation.Bottom;
            }

            RaisePlayerSurfaceVisibilityChanged();
        }
    }

    private void RaisePlayerSurfaceVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsSidebarPlayerVisibleInShell));
        OnPropertyChanged(nameof(IsBottomPlayerVisibleInShell));
    }

    /// <summary>
    /// Right-panel slot is visible in the shell only when the panel is open
    /// AND not torn off into its own window.
    /// </summary>
    public bool IsRightPanelVisibleInShell =>
        IsRightPanelOpen && !Docking.IsRightPanelDetached;

    public bool IsFriendsPanelActive =>
        IsRightPanelOpen && RightPanelMode == Wavee.UI.WinUI.Data.Enums.RightPanelMode.FriendsActivity;

    private bool ShouldHideDockedPlayerForFloatingWindow =>
        Docking.IsPlayerDetached && _settingsService?.Settings.ShowDockedPlayerWithFloatingPlayer != true;

    /// <summary>
    /// Sidebar player widget is visible in the shell when the player is hosted
    /// in the sidebar. By default the popped-out player suppresses the docked
    /// slot, but Settings can opt back into showing both control surfaces.
    /// </summary>
    public bool IsSidebarPlayerVisibleInShell =>
        PlayerLocation == PlayerLocation.Sidebar && !ShouldHideDockedPlayerForFloatingWindow;

    /// <summary>
    /// Bottom player is visible only when it is the selected shell location and
    /// the popped-out player is not suppressing docked controls. Theatre /
    /// Fullscreen presentation also collapse it — the expanded surface owns
    /// the whole window in those modes. Also collapses when the active tab is
    /// VideoPlayerPage — that page has its own scrim transport, duplicating it
    /// at the bottom of the window is just noise.
    /// </summary>
    public bool IsBottomPlayerVisibleInShell =>
        PlayerLocation == PlayerLocation.Bottom
        && !ShouldHideDockedPlayerForFloatingWindow
        && IsNormalPresentation
        && !IsOnVideoPage;

    // ── Floating mini-video-player visibility ────────────────────────────
    //
    // Compound gate: Normal presentation (no Theatre / Fullscreen takeover)
    // AND not on VideoPlayerPage in the active tab AND the mini-VM's own
    // compound says it should be visible (which already includes first-frame
    // gate, suppression flags, user-dismissed flag).
    //
    // Forwarding PropertyChanged from the mini-VM keeps this single XAML
    // binding accurate without the consumer needing to track multiple sources.
    private MiniVideoPlayerViewModel? _miniVideoVm;
    public MiniVideoPlayerViewModel? MiniVideoPlayer =>
        _miniVideoVm ??= ResolveAndSubscribeMiniVideoPlayer();

    private MiniVideoPlayerViewModel? ResolveAndSubscribeMiniVideoPlayer()
    {
        var vm = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<MiniVideoPlayerViewModel>();
        if (vm is not null)
            vm.PropertyChanged += OnMiniVideoPlayerPropertyChanged;
        return vm;
    }

    private void OnMiniVideoPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniVideoPlayerViewModel.IsVisible))
            OnPropertyChanged(nameof(IsMiniPlayerVisibleInShell));
    }

    public bool IsMiniPlayerVisibleInShell =>
        IsNormalPresentation
        && !IsOnVideoPage
        && MiniVideoPlayer?.IsVisible == true;

    // ── Now-playing presentation (Theatre / Fullscreen) ─────────────────
    // Lazily resolved to keep the existing constructor wiring untouched —
    // INowPlayingPresentationService is a small singleton, no DI cycles.
    private INowPlayingPresentationService? _presentation;
    public INowPlayingPresentationService Presentation =>
        _presentation ??= ResolveAndSubscribePresentation();

    private INowPlayingPresentationService ResolveAndSubscribePresentation()
    {
        var svc = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetRequiredService<INowPlayingPresentationService>();
        svc.PropertyChanged += OnPresentationPropertyChanged;
        return svc;
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only respond to the canonical Presentation change; the service
        // raises IsExpanded + IsNormal alongside it (derived flags) but we'd
        // otherwise re-fire every downstream property three times per
        // transition.
        if (e.PropertyName == nameof(INowPlayingPresentationService.Presentation))
        {
            OnPropertyChanged(nameof(IsNormalPresentation));
            OnPropertyChanged(nameof(IsExpandedPresentation));
            OnPropertyChanged(nameof(IsFullscreenPresentation));
            OnPropertyChanged(nameof(IsTheatrePresentation));
            // Chrome visibility helpers depend on presentation — re-raise so
            // the shell page collapses sidebar / tabs / nav / playerbar when
            // we expand into Theatre or Fullscreen.
            RaisePlayerSurfaceVisibilityChanged();
            OnPropertyChanged(nameof(IsTabBarVisibleInShell));
            OnPropertyChanged(nameof(IsNavToolbarVisibleInShell));
            OnPropertyChanged(nameof(IsSidebarVisibleInShell));
            OnPropertyChanged(nameof(IsMiniPlayerVisibleInShell));
        }
    }

    /// <summary>True when the now-playing surface is in its default docked state.</summary>
    public bool IsNormalPresentation => Presentation.IsNormal;

    /// <summary>True when in Theatre OR Fullscreen — chrome should hide.</summary>
    public bool IsExpandedPresentation => Presentation.IsExpanded;

    /// <summary>True specifically in Fullscreen (OS-level fullscreen presenter).</summary>
    public bool IsFullscreenPresentation =>
        Presentation.Presentation == NowPlayingPresentation.Fullscreen;

    /// <summary>True specifically in Theatre (player fills app window, chrome hidden, title bar stays).</summary>
    public bool IsTheatrePresentation =>
        Presentation.Presentation == NowPlayingPresentation.Theatre;

    /// <summary>Tab strip is hidden in Theatre / Fullscreen — the player owns the window.</summary>
    public bool IsTabBarVisibleInShell => IsNormalPresentation;

    /// <summary>Navigation toolbar (search / back / forward) hides in expanded modes.</summary>
    public bool IsNavToolbarVisibleInShell => IsNormalPresentation;

    /// <summary>Sidebar collapses to give the player the full width in expanded modes.</summary>
    public bool IsSidebarVisibleInShell => IsNormalPresentation;

    [ObservableProperty]
    private bool _sidebarPlayerCollapsed;

    [ObservableProperty]
    private ObservableCollection<SidebarItemModel> _sidebarItems = [];

    [ObservableProperty]
    private ISidebarItemModel? _selectedSidebarItem;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isOnHomePage;

    [ObservableProperty]
    private bool _isOnProfilePage;

    // Notification properties backed by INotificationService
    public bool IsNotificationOpen
    {
        get => _notificationService.IsOpen;
        set
        {
            if (!value) _notificationService.Dismiss();
        }
    }
    public string? NotificationMessage => _notificationService.Message;
    public string? NotificationActionLabel => _notificationService.ActionLabel;
    public bool HasNotificationAction => _notificationService.ActionLabel != null;
    public bool IsNotificationActionEnabled => !_notificationService.IsActionBusy;

    /// <summary>
    /// Notification severity passed to the floating toast. Returns the
    /// project's own <see cref="AppNotificationSeverity"/> so the toast
    /// control stays decoupled from <see cref="InfoBarSeverity"/>.
    /// </summary>
    public AppNotificationSeverity NotificationSeverity => _notificationService.Severity;

    public ShellViewModel(
        ILibraryDataService libraryDataService,
        IPlaylistCacheService playlistCache,
        IThemeService themeService,
        INotificationService notificationService,
        ISearchService searchService,
        IPlaybackStateService playbackStateService,
        AppModel appModel,
        IShellSessionService shellSession,
        ISettingsService? settingsService = null,
        IDispatcherService? dispatcher = null,
        ILogger<ShellViewModel>? logger = null,
        PlaylistMosaicService? mosaicService = null,
        Wavee.Local.ILocalLibraryService? localLibrary = null,
        ISpotifyLinkPreviewService? linkPreviewService = null)
    {
        _libraryDataService = libraryDataService;
        _playlistCache = playlistCache;
        _themeService = themeService;
        _notificationService = notificationService;
        _searchService = searchService;
        _playbackStateService = playbackStateService;
        _appModel = appModel;
        _shellSession = shellSession;
        _settingsService = settingsService;
        _dispatcher = dispatcher;
        _logger = logger;
        _mosaicService = mosaicService;
        _localLibrary = AppFeatureFlags.LocalFilesEnabled ? localLibrary : null;
        _linkPreviewService = linkPreviewService;

        // Initialize from AppModel (one-time read)
        _sidebarWidth = appModel.SidebarWidth;
        _sidebarDisplayMode = appModel.SidebarDisplayMode;
        _isSidebarPaneOpen = appModel.IsSidebarPaneOpen;
        _rightPanelWidth = appModel.RightPanelWidth;
        _isRightPanelOpen = appModel.IsRightPanelOpen;
        _rightPanelMode = appModel.RightPanelMode;
        _selectedTabIndex = appModel.TabStripSelectedIndex;
        _playerLocation = appModel.PlayerLocation;
        _sidebarPlayerCollapsed = appModel.SidebarPlayerCollapsed;

        // Listen for right panel toggle requests from PlayerBar
        WeakReferenceMessenger.Default.Register<ToggleRightPanelMessage>(this, (r, m) =>
        {
            ((ShellViewModel)r).ToggleRightPanel(m.Value);
        });

        WeakReferenceMessenger.Default.Register<DockedPlayerWithFloatingPlayerVisibilityChangedMessage>(this, (r, _) =>
        {
            ((ShellViewModel)r).RaisePlayerSurfaceVisibilityChanged();
        });

        // Subscribe to notification service changes to forward to XAML bindings
        _notificationService.PropertyChanged += OnNotificationServicePropertyChanged;

        // Subscribe to playlist changes for reactive updates
        _libraryDataService.PlaylistsChanged += OnPlaylistsChanged;

        // Subscribe to all library data changes (sync complete, Dealer deltas, etc.)
        _libraryDataService.DataChanged += OnLibraryDataChanged;

        // Drag-state hook: while ANY drag is in progress, expand every
        // sidebar folder so deeply-nested folders become drop targets
        // without requiring the user to hover each folder for the
        // auto-expand-on-hover timeout. Restore pre-drag state on drag end.
        // The expansion changes are suppressed from persistence
        // (OnSidebarGroupPropertyChanged checks _suppressExpansionPersistence)
        // so the user's saved layout isn't trampled.
        var dragStateService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<Wavee.UI.WinUI.DragDrop.DragStateService>();
        if (dragStateService is not null)
            dragStateService.DragStateChanged += OnGlobalDragStateChanged;

        // Per-playlist change → sidebar mosaic refresh. PlaylistDiffApplier
        // updates Items in the cache after a Mercury push, but the sidebar's
        // cached IconSource keeps pointing at the old composite forever
        // (LazyIconSourceLoader is cleared on first load). We listen for
        // Updated events here, drop the in-flight + on-disk mosaic via
        // PlaylistMosaicService.Invalidate, then kick off a fresh build and
        // swap model.IconSource — the SidebarItem control listens for that
        // PropertyChanged and re-renders the icon.
        // Subscribe regardless of mosaic-service availability — the handler's
        // first phase promotes real covers from the cache (works with or without
        // the mosaic service), and the second phase only kicks in when a mosaic
        // is actually appropriate.
        _playlistMosaicChangesSubscription = _playlistCache.Changes
            .Where(static evt => evt.Kind == PlaylistChangeKind.Updated
                              && !string.IsNullOrEmpty(evt.Uri))
            .Subscribe(evt => OnPlaylistContentsChanged(evt.Uri));

        // Capture UI thread dispatcher for background → UI marshalling
        // Dispatcher captured via DI
        WeakReferenceMessenger.Default.Register<Data.Messages.LibrarySyncStartedMessage>(this, (_, _) =>
        {
            _dispatcher?.TryEnqueue(() =>
            {
                _logger?.LogDebug("Sidebar: sync started — clearing badges");
                ClearLibraryBadges();
            });
        });
        WeakReferenceMessenger.Default.Register<Data.Messages.LibrarySyncFailedMessage>(this, (_, msg) =>
        {
            _dispatcher?.TryEnqueue(() =>
            {
                _logger?.LogWarning("Sidebar: sync failed — {Error}", msg.Value);
                ShowNotification(AppLocalization.Format("Shell_LibrarySyncFailed", msg.Value));
            });
        });

        // Initial library load must wait for auth+sync to complete — rootlist lookup
        // requires an authenticated username, so firing this from the constructor
        // races the auth pipeline and produces a spurious "Failed to load library data"
        // error on every cold start.
        WeakReferenceMessenger.Default.Register<Data.Messages.LibrarySyncCompletedMessage>(this, (_, _) =>
        {
            _dispatcher?.TryEnqueue(() => _ = LoadLibraryDataAsync());
        });

        // On sign-out, wipe the signed-in user's sidebar state (badges + playlists)
        // so the next user doesn't briefly see stale counts/items before their sync lands.
        WeakReferenceMessenger.Default.Register<AuthStatusChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value is AuthStatus.LoggedOut or AuthStatus.SessionExpired)
            {
                _dispatcher?.TryEnqueue(() =>
                {
                    _logger?.LogDebug("Sidebar: auth status {Status} — clearing library state", msg.Value);
                    ClearLibrarySidebar();
                });
            }
        });

        InitializeSidebarItems();
        ApplyPersistedSidebarState();
        TabInstances.CollectionChanged += OnTabInstancesCollectionChanged;
        InitializeTabSleepTimer();
    }

    private void OnNotificationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(INotificationService.IsOpen):
                OnPropertyChanged(nameof(IsNotificationOpen));
                break;
            case nameof(INotificationService.Message):
                OnPropertyChanged(nameof(NotificationMessage));
                break;
            case nameof(INotificationService.Severity):
                OnPropertyChanged(nameof(NotificationSeverity));
                break;
            case nameof(INotificationService.ActionLabel):
                OnPropertyChanged(nameof(NotificationActionLabel));
                OnPropertyChanged(nameof(HasNotificationAction));
                break;
            case nameof(INotificationService.IsActionBusy):
                OnPropertyChanged(nameof(IsNotificationActionEnabled));
                break;
        }
    }

    public void ShowNotification(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        var mapped = severity switch
        {
            InfoBarSeverity.Informational => AppNotificationSeverity.Informational,
            InfoBarSeverity.Success => AppNotificationSeverity.Success,
            InfoBarSeverity.Warning => AppNotificationSeverity.Warning,
            InfoBarSeverity.Error => AppNotificationSeverity.Error,
            _ => AppNotificationSeverity.Error
        };
        _notificationService.Show(message, mapped);
    }

    private void ClearLibraryBadges()
    {
        var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
        if (librarySection?.Children is ObservableCollection<SidebarItemModel> libraryChildren)
        {
            foreach (var item in libraryChildren)
                item.BadgeCount = null;
        }
    }

    private void ClearLibrarySidebar()
    {
        ClearLibraryBadges();

        var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
        if (playlistsSection?.Children is ObservableCollection<SidebarItemModel> playlistChildren)
        {
            playlistChildren.Clear();
        }

        var pinnedSection = SidebarItems.FirstOrDefault(x => x.Tag == "Pinned");
        if (pinnedSection?.Children is ObservableCollection<SidebarItemModel> pinnedChildren)
        {
            pinnedChildren.Clear();
        }
    }

    /// <summary>
    /// Cancelled on every new OnPlaylistsChanged tick so bursts of dealer events collapse
    /// into a single rebuild ~<see cref="PlaylistRefreshDebounceMs"/> after the last event.
    /// Rebuilding the sidebar is expensive (N SidebarItemModels + connector strips); the
    /// previous "rebuild on every event" path was the main culprit for app-wide slowness.
    /// </summary>
    private CancellationTokenSource? _playlistRefreshCts;

    // Subscription that re-builds a sidebar mosaic when its playlist's items
    // change (Mercury push → PlaylistDiffApplier mutates Items → Updated event
    // fires → we rebuild the composite). Without this, the cached mosaic
    // keeps showing the old top-4 album covers until app restart.
    private IDisposable? _playlistMosaicChangesSubscription;
    private const int PlaylistRefreshDebounceMs = 250;

    private void OnLibraryDataChanged(object? sender, EventArgs e)
    {
        // DataChanged fires for lots of unrelated things (liked-songs save state, Dealer
        // deltas on non-playlist topics, etc). We deliberately don't rebuild the whole
        // sidebar here — only OnPlaylistsChanged does the heavy work. But the four
        // library badge counts (Albums / Artists / Liked Songs / Podcasts) ARE cheap to
        // refresh, and missing one means the sidebar shows a stale number after the user
        // likes/saves/follows something. So: stats-only refresh here.
        _dispatcher?.TryEnqueue(async () =>
        {
            try { await RefreshLibraryBadgesAsync(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Sidebar badge refresh failed"); }
            try { await RefreshPinnedAsync(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Sidebar pinned refresh failed"); }
        });
    }

    private async Task RefreshLibraryBadgesAsync()
    {
        var stats = await _libraryDataService.GetStatsAsync();
        var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
        if (librarySection?.Children is not ObservableCollection<SidebarItemModel> children) return;

        var albums = children.FirstOrDefault(x => x.Tag as string == "Albums");
        if (albums != null) albums.BadgeCount = stats.AlbumCount;

        var artists = children.FirstOrDefault(x => x.Tag as string == "Artists");
        if (artists != null) artists.BadgeCount = stats.ArtistCount;

        var liked = children.FirstOrDefault(x => x.Tag as string == "LikedSongs");
        if (liked != null) liked.BadgeCount = stats.LikedSongsCount;

        var podcasts = children.FirstOrDefault(x => x.Tag as string is "Podcasts" or "YourEpisodes");
        if (podcasts != null) podcasts.BadgeCount = stats.PodcastCount;
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        // Debounce: cancel any pending refresh and schedule a new one. Rapid dealer bursts
        // (e.g. five Add-track events in 50ms on a collaborative playlist) collapse into
        // one rebuild after the quiet period.
        var previous = Interlocked.Exchange(ref _playlistRefreshCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var token = _playlistRefreshCts!.Token;
        _dispatcher?.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(PlaylistRefreshDebounceMs, token);
                await RefreshPlaylistsAsync();
            }
            catch (OperationCanceledException) { /* superseded by a newer event */ }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to handle playlists change event");
                ShowNotification(AppLocalization.GetString("Shell_RefreshPlaylistsFailed"));
            }
        });
    }

    private async Task RefreshPlaylistsAsync()
    {
        try
        {
            // Phase 1 — cache-only. SQLite + hot in-memory only, never the network.
            // When the user has been signed in before, this hydrates the sidebar in
            // a few ms. When the cache is empty (cold launch / signed-out), both
            // helpers return null and the shimmer stays visible until Phase 2.
            var cachedPlaylistsTask = _libraryDataService.TryGetUserPlaylistsFromCacheAsync();
            var cachedTreeTask = _playlistCache.TryGetRootlistTreeFromCacheAsync();
            await Task.WhenAll(cachedPlaylistsTask, cachedTreeTask);

            var cachedPlaylists = await cachedPlaylistsTask;
            var cachedTree = await cachedTreeTask;
            if (cachedPlaylists is not null && cachedTree is not null)
            {
                PopulatePlaylistsSidebar(cachedPlaylists, cachedTree);
                ClearPlaylistsLoadingState();
            }

            // Phase 2 — network-backed refresh. Runs to completion even when Phase 1
            // already painted from cache; the smart diff in PopulatePlaylistsSidebar
            // reuses existing SidebarItemModels by Tag, so unchanged rows do not
            // flicker. Network failures here surface as caught exceptions but the
            // sidebar keeps whatever Phase 1 already rendered.
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();
            var treeTask = _playlistCache.GetRootlistTreeAsync();
            await Task.WhenAll(playlistsTask, treeTask);
            PopulatePlaylistsSidebar(await playlistsTask, await treeTask);
            ClearPlaylistsLoadingState();
        }
        catch (Exception ex)
        {
            // Network refresh failed — still drop the loading state so the user
            // isn't left staring at perpetual shimmer when the cache was empty.
            ClearPlaylistsLoadingState();
            _logger?.LogError(ex, "Failed to refresh playlists from service");
            throw;
        }
    }

    private void ClearPlaylistsLoadingState()
    {
        var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
        if (playlistsSection is not null && playlistsSection.IsLoadingChildren)
            playlistsSection.IsLoadingChildren = false;
    }

    private void InitializeSidebarItems()
    {
        SidebarItems =
        [
            // Pinned section (collapsible, dynamic items)
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarPinned"),
                Tag = "Pinned",
                IsExpanded = true,
                IsSectionHeader = true,
                ShowEmptyPlaceholder = true,
                Children = new ObservableCollection<SidebarItemModel>
                {
                    // Dynamic pinned items will be populated here
                }
            },
            // Your Library section (collapsible, NO playlists)
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarYourLibrary"),
                Tag = "YourLibrary",
                IsExpanded = true,
                IsSectionHeader = true,
                ShowCompactSeparatorBefore = true,
                Children = new ObservableCollection<SidebarItemModel>
                {
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarAlbums"),
                        IconSource = new FontIconSource { Glyph = "\uE93C" },
                        Tag = "Albums",
                        BadgeCount = 0
                    },
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarArtists"),
                        IconSource = new FontIconSource { Glyph = "\uE77B" },
                        Tag = "Artists",
                        BadgeCount = 0
                    },
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarLikedSongs"),
                        IconSource = new FontIconSource { Glyph = "\uEB52" },
                        Tag = "LikedSongs",
                        BadgeCount = 0,
                        ShowPinToggleButton = true
                    },
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarPodcasts"),
                        IconSource = new FontIconSource { Glyph = "\uEC05" },
                        Tag = "Podcasts",
                        BadgeCount = 0,
                        ShowPinToggleButton = true
                    },
                    // Local files landing page. The typed shelves stay one click
                    // deeper inside LocalLibraryPage instead of occupying four
                    // separate sidebar rows. FontIconSource (not SymbolIconSource)
                    // for parity with its siblings — SymbolIconSource carries a
                    // different default size/margin that visibly stretches the
                    // icon column and squeezes the label.
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarLocalFiles"),
                        IconSource = new FontIconSource { Glyph = FluentGlyphs.Folder },
                        Tag = "LocalFiles",
                        BadgeCount = null,
                        IsEnabled = AppFeatureFlags.LocalFilesEnabled,
                        ToolTip = AppFeatureFlags.LocalFilesEnabled
                            ? AppLocalization.GetString("Shell_SidebarLocalFiles")
                            : "Coming soon after the initial beta release",
                        ItemDecorator = AppFeatureFlags.LocalFilesEnabled ? null : CreateComingSoonBadge()
                    }
                }
            },
            // Playlists section (collapsible). IsLoadingChildren=true on cold boot
            // so the sidebar shows shimmer rows the instant it realizes, before any
            // cache or network read has completed. RefreshPlaylistsAsync flips this
            // back to false as soon as either tier yields a result.
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarPlaylists"),
                Tag = "Playlists",
                IsExpanded = true,
                IsSectionHeader = true,
                ShowCompactSeparatorBefore = true,
                IsLoadingChildren = true,
                ShowEmptyPlaceholder = true,
                EmptyPlaceholderText = AppLocalization.GetString("Shell_SidebarNoPlaylists"),
                ItemDecorator = CreatePlaylistsAddButton(),
                Children = new ObservableCollection<SidebarItemModel>
                {
                    // User playlists will be populated dynamically
                }
            }
        ];

        foreach (var group in SidebarItems)
            group.PropertyChanged += OnSidebarGroupPropertyChanged;
    }

    private void ApplyPersistedSidebarState()
    {
        foreach (var group in SidebarItems)
        {
            ApplyPersistedSidebarState(group);
        }

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
            SelectedSidebarItem = FindSidebarItemByTag(selectedTag);
    }

    private SidebarItemModel? FindSidebarItemByTag(string tag)
    {
        return FindSidebarItemByTag(SidebarItems, tag);
    }

    /// <summary>
    /// Sync the sidebar selection to the playlist identified by <paramref name="uriOrId"/>.
    /// Accepts a bare playlist id, a Spotify URI (<c>spotify:playlist:xxx</c>), or a
    /// <see cref="ContentNavigationParameter"/> carrying either. The id segment after the
    /// last <c>:</c> is extracted before looking up the sidebar row.
    /// Clears the selection when no sidebar row matches — e.g. a search-opened playlist
    /// that isn't in the user's library.
    /// </summary>
    public void SyncSidebarSelectionToPlaylist(object? uriOrId)
    {
        var s = uriOrId switch
        {
            ContentNavigationParameter nav => nav.Uri,
            string value => value,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(s))
        {
            // Only assign if not already null — otherwise the setter still fires
            // PropertyChanged and cascades to every realized SidebarItem.
            if (SelectedSidebarItem is not null)
                SelectedSidebarItem = null;
            return;
        }

        var trimmed = s.Trim();
        var match = FindSidebarItemByTag(trimmed);
        if (match is null)
        {
            var lastColon = trimmed.LastIndexOf(':');
            var id = lastColon >= 0 ? trimmed[(lastColon + 1)..] : trimmed;

            if (!string.Equals(id, trimmed, StringComparison.Ordinal))
                match = FindSidebarItemByTag(id);

            if (match is null && !trimmed.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                match = FindSidebarItemByTag($"spotify:playlist:{id}");
        }

        // De-dup: if the resolved item is already selected, skip the assignment.
        // The SelectedSidebarItem setter fires PropertyChanged unconditionally, and
        // every realized SidebarItem reacts via the SidebarView.SelectedItemProperty
        // PropertyChangedCallback (running VisualStateManager.GoToState + folder
        // glyph swaps synchronously). Without this guard, every nav — including
        // tab-switches and re-clicks of the currently-selected playlist — produces
        // a visible folder-flash cascade across the entire sidebar tree.
        if (ReferenceEquals(SelectedSidebarItem, match)) return;

        SelectedSidebarItem = match;
    }

    public void SyncSidebarSelectionToTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            if (SelectedSidebarItem is not null)
                SelectedSidebarItem = null;
            return;
        }

        var match = FindSidebarItemByTag(tag);
        if (ReferenceEquals(SelectedSidebarItem, match)) return;

        SelectedSidebarItem = match;
    }

    private void OnSidebarGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SidebarItemModel group
            || e.PropertyName != nameof(SidebarItemModel.IsExpanded)
            || string.IsNullOrWhiteSpace(group.Tag))
        {
            return;
        }

        // Folders: swap Fluent Folder (E8B7) ↔ FolderOpen (E838) so the tree glyph matches state.
        // FontFamily re-pinned on each replacement — without it the new IconSource
        // inherits ContentControlThemeFontFamily (a text font) and the glyph renders as tofu.
        if (group.IsFolder)
            group.IconSource = new FontIconSource
            {
                Glyph = group.IsExpanded ? FluentGlyphs.FolderOpen : FluentGlyphs.Folder,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
            };

        // Suppress persistence during drag-driven auto-expand. The drag flow
        // owns the snapshot/restore; if we let it write through to
        // _shellSession the user's saved layout gets clobbered when they end
        // a drag with folders that were collapsed before the drag started.
        if (_suppressExpansionPersistence) return;

        _shellSession.UpdateSidebarGroupExpansion(group.Tag!, group.IsExpanded);
    }

    // ── Drag-time folder auto-expand ───────────────────────────────────────

    private Dictionary<string, bool>? _preDragFolderExpansion;
    private bool _suppressExpansionPersistence;

    private void OnGlobalDragStateChanged(bool isDragging)
    {
        // Marshal to UI thread — SidebarItemModel changes flow into XAML bindings.
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                 ?? DispatcherQueueIfAvailable();
        if (dq is null)
        {
            ApplyDragExpansion(isDragging);
            return;
        }
        dq.TryEnqueue(() => ApplyDragExpansion(isDragging));
    }

    private static Microsoft.UI.Dispatching.DispatcherQueue? DispatcherQueueIfAvailable()
        => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private void ApplyDragExpansion(bool isDragging)
    {
        if (isDragging)
        {
            // Re-entrancy guard: rapid drag-start/end pairs while a snapshot
            // is mid-restore would mix states. Skip if a snapshot exists.
            if (_preDragFolderExpansion is not null) return;

            _preDragFolderExpansion = new Dictionary<string, bool>(StringComparer.Ordinal);
            _suppressExpansionPersistence = true;
            try
            {
                ForEachFolder(item =>
                {
                    if (string.IsNullOrEmpty(item.Tag)) return;
                    _preDragFolderExpansion[item.Tag!] = item.IsExpanded;
                    if (!item.IsExpanded) item.IsExpanded = true;
                });
            }
            finally
            {
                _suppressExpansionPersistence = false;
            }
        }
        else
        {
            if (_preDragFolderExpansion is null) return;

            _suppressExpansionPersistence = true;
            try
            {
                foreach (var (tag, prev) in _preDragFolderExpansion)
                {
                    var item = FindSidebarItemByTag(tag);
                    if (item is null) continue;
                    if (item.IsExpanded != prev) item.IsExpanded = prev;
                }
            }
            finally
            {
                _suppressExpansionPersistence = false;
                _preDragFolderExpansion = null;
            }
        }
    }

    /// <summary>
    /// Walk every folder-kind sidebar item (including nested subfolders) and
    /// invoke the action. Used by the drag-time auto-expand path.
    /// </summary>
    private void ForEachFolder(Action<SidebarItemModel> action)
    {
        foreach (var root in SidebarItems)
            WalkInto(root);

        void WalkInto(SidebarItemModel item)
        {
            if (item.IsFolder) action(item);
            if (item.Children is System.Collections.IEnumerable kids)
            {
                foreach (var c in kids)
                    if (c is SidebarItemModel child) WalkInto(child);
            }
        }
    }

    private static Microsoft.UI.Xaml.FrameworkElement CreateComingSoonBadge()
    {
        static Brush ResourceBrush(string key, Color fallback)
        {
            return Application.Current?.Resources.TryGetValue(key, out var value) == true
                   && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);
        }

        return new Border
        {
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(10),
            Background = ResourceBrush("SubtleFillColorSecondaryBrush", Microsoft.UI.ColorHelper.FromArgb(0x22, 0x7F, 0x7F, 0x7F)),
            Child = new TextBlock
            {
                Text = "Soon",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray)
            }
        };
    }

    private Microsoft.UI.Xaml.FrameworkElement CreatePlaylistsAddButton()
    {
        var menuFlyout = new Microsoft.UI.Xaml.Controls.MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };

        var mediaPlayerIconsFont = Microsoft.UI.Xaml.Application.Current?.Resources?.TryGetValue("MediaPlayerIconsFontFamily", out var fontObj) == true
            ? fontObj as Microsoft.UI.Xaml.Media.FontFamily
            : null;

        _newPlaylistMenuItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = AppLocalization.GetString("Shell_NewPlaylist"),
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = FluentGlyphs.CreatePlaylist }
        };
        _newPlaylistMenuItem.Click += NewPlaylistMenuItem_Click;

        _newFolderMenuItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = AppLocalization.GetString("Shell_NewFolder"),
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = FluentGlyphs.CreateFolder }
        };
        _newFolderMenuItem.Click += NewFolderMenuItem_Click;

        menuFlyout.Items.Add(_newPlaylistMenuItem);
        menuFlyout.Items.Add(_newFolderMenuItem);

        // Plain Button + Flyout (not SplitButton) so a click anywhere on the icon opens
        // the menu \u2014 same affordance whether the sidebar is expanded or compact (where
        // the only the decorator survives the CompactGroupHeaderWithDecorator state).
        _playlistsAddButton = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = new Microsoft.UI.Xaml.Controls.FontIcon
            {
                Glyph = FluentGlyphs.CreatePlaylist,
                FontSize = 12
            },
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            Padding = new Microsoft.UI.Xaml.Thickness(0),
            MinWidth = 24,
            MinHeight = 24,
            Width = 24,
            Height = 24,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            Flyout = menuFlyout,
            // Suppress the default WinUI focus rectangle — it's a saturated
            // accent-coloured rect that reads identically to the sidebar's
            // selected-item border, making the "+" look perpetually selected
            // after any interaction lands focus on it.
            UseSystemFocusVisuals = false
        };

        return _playlistsAddButton;
    }

    private void NewPlaylistMenuItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Helpers.Navigation.NavigationHelpers.OpenCreatePlaylist(isFolder: false);
    }

    private void NewFolderMenuItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Helpers.Navigation.NavigationHelpers.OpenCreatePlaylist(isFolder: true);
    }

    private async Task LoadLibraryDataAsync()
    {
        try
        {
            // Stats run in parallel with the two-phase playlist refresh. The
            // playlists fan-out (cache + network) is owned by RefreshPlaylistsAsync
            // so cold-launch shimmer / warm-launch instant-hydrate behave identically
            // here and on subsequent PlaylistsChanged events.
            var statsTask = _libraryDataService.GetStatsAsync();
            var playlistsRefreshTask = RefreshPlaylistsAsync();
            var pinnedRefreshTask = RefreshPinnedAsync();

            await Task.WhenAll(statsTask, playlistsRefreshTask, pinnedRefreshTask);

            var stats = await statsTask;

            // Update "Your Library" section badges
            var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
            if (librarySection?.Children is ObservableCollection<SidebarItemModel> libraryChildren)
            {
                var albumsItem = libraryChildren.FirstOrDefault(x => x.Tag as string == "Albums");
                if (albumsItem != null) albumsItem.BadgeCount = stats.AlbumCount;

                var artistsItem = libraryChildren.FirstOrDefault(x => x.Tag as string == "Artists");
                if (artistsItem != null) artistsItem.BadgeCount = stats.ArtistCount;

                var likedItem = libraryChildren.FirstOrDefault(x => x.Tag as string == "LikedSongs");
                if (likedItem != null) likedItem.BadgeCount = stats.LikedSongsCount;

                var podcastsItem = libraryChildren.FirstOrDefault(x => x.Tag as string is "Podcasts" or "YourEpisodes");
                if (podcastsItem != null) podcastsItem.BadgeCount = stats.PodcastCount;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load library data");
            ShowNotification(AppLocalization.GetString("Shell_LoadLibraryFailed"));
        }
    }

    private void PopulatePlaylistsSidebar(
        IReadOnlyList<PlaylistSummaryDto> playlists,
        RootlistTree tree)
    {
        // Smart key-based diff. Reuses existing SidebarItemModel instances by Tag,
        // inserts new ones in place, moves reordered ones, and trims removed ones.
        // Replaces the previous Clear() + walk-and-append, which made the sidebar
        // flash on every refresh even when the fresh data was identical to what
        // was already painted (the common case after the cache→network fan-out).
        var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
        if (playlistsSection?.Children is not ObservableCollection<SidebarItemModel> playlistChildren)
            return;

        var playlistLookup = playlists.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var target = BuildPlaylistTargetNodes(tree.Root, playlistLookup);
        DiffPlaylistCollection(playlistChildren, target, depth: 0);

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
        {
            var match = FindSidebarItemByTag(selectedTag);
            if (!ReferenceEquals(SelectedSidebarItem, match))
                SelectedSidebarItem = match;
        }
    }

    private async Task RefreshPinnedAsync()
    {
        try
        {
            var items = await _libraryDataService.GetPinnedItemsAsync();
            PopulatePinnedSidebar(items);
            SyncCanonicalRowsPinnedState();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh pinned items");
        }
    }

    /// <summary>
    /// Flips the <c>IsPinned</c> flag on the canonical Your-Library Liked Songs
    /// and Podcasts rows based on whether their corresponding pseudo-URIs are in
    /// the pinned set. Drives the pin/unpin glyph on the always-visible toggle
    /// button.
    /// </summary>
    private void SyncCanonicalRowsPinnedState()
    {
        var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
        if (librarySection?.Children is not ObservableCollection<SidebarItemModel> children) return;

        foreach (var row in children)
        {
            if (row.Tag == "LikedSongs")
            {
                row.IsPinned = _libraryDataService.IsPinned("spotify:collection");
            }
            else if (row.Tag == "Podcasts" || row.Tag == "YourEpisodes")
            {
                row.IsPinned = _libraryDataService.IsPinned("spotify:collection:your-episodes");
            }
        }
    }

    /// <summary>
    /// Handles a click on the inline pin/unpin button on any sidebar row.
    /// For Pinned-section rows (Tag = the raw URI), always unpins. For
    /// canonical Your-Library rows (Tag = "LikedSongs" / "Podcasts"), maps to
    /// the pseudo-URI and unpins only when currently pinned. Optimistic: the
    /// local DB is updated by the service before the network call.
    /// </summary>
    public async Task HandleSidebarPinButtonAsync(SidebarItemModel model)
    {
        if (model is null) return;

        if (model.ShowUnpinButton)
        {
            // Pinned-section row — Tag IS the URI we want to unpin.
            if (string.IsNullOrEmpty(model.Tag)) return;
            try
            {
                var ok = await _libraryDataService.UnpinAsync(model.Tag);
                if (!ok)
                    NotifyPinFailure(unpinned: true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unpin failed for {Uri}", model.Tag);
                NotifyPinFailure(unpinned: true);
            }
            return;
        }

        if (!model.ShowPinToggleButton) return;

        // Canonical YL row — map the row tag to its pseudo-URI.
        var uri = model.Tag switch
        {
            "LikedSongs" => "spotify:collection",
            "Podcasts" or "YourEpisodes" => "spotify:collection:your-episodes",
            _ => null
        };
        if (uri is null) return;

        var wasPinned = _libraryDataService.IsPinned(uri);
        if (!wasPinned)
            return;

        try
        {
            var ok = await _libraryDataService.UnpinAsync(uri);
            if (!ok)
                NotifyPinFailure(unpinned: wasPinned);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pin toggle failed for {Uri}", uri);
            NotifyPinFailure(unpinned: wasPinned);
        }
    }

    private void NotifyPinFailure(bool unpinned)
    {
        // Toast surfaces the rollback to the user: the optimistic local write
        // already reverted inside SpotifyLibraryService, so the sidebar shows
        // the correct (unchanged) state — this message just explains why.
        var message = unpinned
            ? "Couldn't unpin from the sidebar. Check your connection and try again."
            : "Couldn't pin to the sidebar. Check your connection and try again.";
        ShowNotification(message, InfoBarSeverity.Warning);
    }

    private void PopulatePinnedSidebar(IReadOnlyList<PinnedItemDto> items)
    {
        var pinnedSection = SidebarItems.FirstOrDefault(x => x.Tag == "Pinned");
        if (pinnedSection?.Children is not ObservableCollection<SidebarItemModel> children)
            return;

        // Flat key-based diff — same shape as DiffPlaylistCollection but without
        // folder recursion. Reuses existing rows by URI so selection survives,
        // and updates Text/Image in place when an unchanged row's title comes
        // back from a fresh metadata fetch.
        for (int i = 0; i < items.Count; i++)
        {
            var t = items[i];
            if (i < children.Count && string.Equals(children[i].Tag, t.Uri, StringComparison.Ordinal))
            {
                UpdatePinnedRow(children[i], t);
                continue;
            }

            int found = -1;
            for (int j = i + 1; j < children.Count; j++)
            {
                if (string.Equals(children[j].Tag, t.Uri, StringComparison.Ordinal))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                children.Move(found, i);
                UpdatePinnedRow(children[i], t);
            }
            else
            {
                children.Insert(i, BuildPinnedRow(t));
            }
        }

        while (children.Count > items.Count)
            children.RemoveAt(children.Count - 1);

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
        {
            var match = FindSidebarItemByTag(selectedTag);
            if (!ReferenceEquals(SelectedSidebarItem, match))
                SelectedSidebarItem = match;
        }
    }

    private static void UpdatePinnedRow(SidebarItemModel current, PinnedItemDto t)
    {
        current.Text = t.Title;

        // ImageUrl on the model is captured for parity with playlist rows; if
        // the cover URL has changed (metadata backfill landed) reseat the icon.
        if (!string.Equals(current.ImageUrl, t.ImageUrl, StringComparison.Ordinal))
        {
            current.ImageUrl = t.ImageUrl;
            current.IconSource = CreatePinnedIconSource(t);
        }
    }

    private static SidebarItemModel BuildPinnedRow(PinnedItemDto t)
    {
        return new SidebarItemModel
        {
            Text = t.Title,
            Tag = t.Uri,
            ImageUrl = t.ImageUrl,
            IconSource = CreatePinnedIconSource(t),
            Depth = 0,
            ShowUnpinButton = true
        };
    }

    private static IconSource CreatePinnedIconSource(PinnedItemDto t)
    {
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(t.ImageUrl);
        if (!string.IsNullOrEmpty(httpsUrl))
        {
            return new ImageIconSource
            {
                ImageSource = new BitmapImage
                {
                    UriSource = new Uri(httpsUrl),
                    DecodePixelWidth = 44
                }
            };
        }

        // No cover yet — fall back to a kind-appropriate Fluent glyph so the
        // row reads correctly while the metadata backfill is in flight.
        // Liked Songs / Your Episodes are pseudo-URIs with no cover at all, so
        // the glyph IS the icon — picked to match their "Your Library" siblings.
        var glyph = t.Kind switch
        {
            PinnedItemKind.Artist => FluentGlyphs.Artist,
            PinnedItemKind.Album => FluentGlyphs.Album,
            PinnedItemKind.Show => FluentGlyphs.Radio,
            PinnedItemKind.LikedSongs => FluentGlyphs.HeartFilled,
            PinnedItemKind.YourEpisodes => FluentGlyphs.Radio,
            _ => FluentGlyphs.Playlist
        };
        return new FontIconSource { Glyph = glyph };
    }

    /// <summary>
    /// Ephemeral plan of what each position in a sidebar collection should look
    /// like after a refresh. Carries enough state to either mutate an existing
    /// row in place or build a fresh one without re-walking the rootlist tree.
    /// </summary>
    private sealed record PlaylistTargetNode(
        string Key,
        string Name,
        bool IsFolder,
        PlaylistSummaryDto? Summary,
        IReadOnlyList<PlaylistTargetNode> Children);

    private static IReadOnlyList<PlaylistTargetNode> BuildPlaylistTargetNodes(
        RootlistNode node,
        IReadOnlyDictionary<string, PlaylistSummaryDto> playlistLookup)
    {
        var list = new List<PlaylistTargetNode>();
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case RootlistChildPlaylist playlist:
                    if (playlistLookup.TryGetValue(playlist.Uri, out var summary))
                    {
                        list.Add(new PlaylistTargetNode(
                            Key: summary.Id,
                            Name: summary.Name,
                            IsFolder: false,
                            Summary: summary,
                            Children: Array.Empty<PlaylistTargetNode>()));
                    }
                    break;

                case RootlistChildFolder folder:
                    list.Add(new PlaylistTargetNode(
                        Key: $"folder:{folder.Folder.Id}",
                        Name: folder.Folder.Name ?? string.Empty,
                        IsFolder: true,
                        Summary: null,
                        Children: BuildPlaylistTargetNodes(folder.Folder, playlistLookup)));
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// Walks the target list position-by-position against the live collection:
    /// in-place updates matching keys, Move-s already-existing keys into position,
    /// and Insert-s newcomers. Trailing items beyond the target length are removed
    /// at the end. Recurses into folder children so a moved folder retains its
    /// expanded state and its children diff against the folder's existing
    /// ObservableCollection rather than being torn down.
    /// </summary>
    private void DiffPlaylistCollection(
        ObservableCollection<SidebarItemModel> current,
        IReadOnlyList<PlaylistTargetNode> target,
        int depth)
    {
        for (int i = 0; i < target.Count; i++)
        {
            var t = target[i];
            if (i < current.Count && string.Equals(current[i].Tag, t.Key, StringComparison.Ordinal))
            {
                UpdatePlaylistMutableFields(current[i], t, depth);
            }
            else
            {
                int found = -1;
                for (int j = i + 1; j < current.Count; j++)
                {
                    if (string.Equals(current[j].Tag, t.Key, StringComparison.Ordinal))
                    {
                        found = j;
                        break;
                    }
                }

                if (found >= 0)
                {
                    current.Move(found, i);
                    UpdatePlaylistMutableFields(current[i], t, depth);
                }
                else
                {
                    current.Insert(i, BuildSidebarItemFromTarget(t, depth));
                }
            }

            if (t.IsFolder && current[i].Children is ObservableCollection<SidebarItemModel> nested)
            {
                DiffPlaylistCollection(nested, t.Children, depth + 1);
            }
        }

        while (current.Count > target.Count)
        {
            current.RemoveAt(current.Count - 1);
        }
    }

    private void UpdatePlaylistMutableFields(SidebarItemModel current, PlaylistTargetNode t, int depth)
    {
        // SetProperty short-circuits on equality, so PropertyChanged only fires
        // when a field actually changed — keeps unchanged rows from animating.
        current.Depth = depth;

        if (t.IsFolder)
        {
            var newName = string.IsNullOrWhiteSpace(t.Name)
                ? AppLocalization.GetString("Shell_NewFolder")
                : t.Name;
            current.Text = newName;
            return;
        }

        if (t.Summary is { } summary)
        {
            current.Text = summary.Name;
            current.BadgeCount = summary.TrackCount;
            current.IsOwner = summary.IsOwner;
        }
    }

    private SidebarItemModel BuildSidebarItemFromTarget(PlaylistTargetNode t, int depth)
    {
        if (t.IsFolder)
        {
            var children = new ObservableCollection<SidebarItemModel>();
            var folderItem = new SidebarItemModel
            {
                Text = string.IsNullOrWhiteSpace(t.Name)
                    ? AppLocalization.GetString("Shell_NewFolder")
                    : t.Name,
                IconSource = new FontIconSource
                {
                    Glyph = FluentGlyphs.FolderOpen,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                },
                Tag = t.Key,
                IsExpanded = true,
                Depth = depth,
                IsFolder = true,
                ShowEmptyPlaceholder = true,
                EmptyPlaceholderText = AppLocalization.GetString("Shell_SidebarFolderEmpty"),
                Children = children,
                DropPredicate = FolderDropPredicate(t.Key),
            };
            folderItem.PropertyChanged += OnSidebarGroupPropertyChanged;
            ApplyPersistedSidebarState(folderItem);
            DiffPlaylistCollection(children, t.Children, depth + 1);
            return folderItem;
        }

        var item = CreatePlaylistSidebarItem(t.Summary!);
        item.Depth = depth;
        return item;
    }

    private SidebarItemModel BuildFolderSidebarItem(
        RootlistNode folder,
        IReadOnlyDictionary<string, PlaylistSummaryDto> playlistLookup,
        int depth)
    {
        var children = new ObservableCollection<SidebarItemModel>();
        AppendNodeChildren(folder, children, playlistLookup, depth + 1);

        var folderItem = new SidebarItemModel
        {
            Text = string.IsNullOrWhiteSpace(folder.Name)
                ? AppLocalization.GetString("Shell_NewFolder")
                : folder.Name,
            // Pin the font explicitly — otherwise the glyph falls through to whatever
            // ContentControlThemeFontFamily resolves to, which is _not_ a symbol font and
            // renders E838/E8B7 as tofu. Segoe Fluent Icons ships with Windows 11; MDL2
            // Assets is the Windows-10 fallback (both contain these codepoints).
            IconSource = new FontIconSource
            {
                Glyph = FluentGlyphs.FolderOpen,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
            },
            Tag = $"folder:{folder.Id}",
            IsExpanded = true,
            Depth = depth,
            IsFolder = true,
            ShowEmptyPlaceholder = true,
            EmptyPlaceholderText = AppLocalization.GetString("Shell_SidebarFolderEmpty"),
            Children = children,
            DropPredicate = FolderDropPredicate($"folder:{folder.Id}"),
        };
        folderItem.PropertyChanged += OnSidebarGroupPropertyChanged;
        ApplyPersistedSidebarState(folderItem);
        return folderItem;
    }

    /// <summary>
    /// Drop-eligibility predicate for sidebar folder rows. Folders accept any
    /// playlist (nest into folder) or any sibling sidebar row (reorder around
    /// the folder). They never accept tracks directly — tracks land on
    /// playlists, not folders.
    /// </summary>
    private static Func<Wavee.UI.Services.DragDrop.IDragPayload, bool> FolderDropPredicate(string folderTag) =>
        payload => payload switch
        {
            Wavee.UI.Services.DragDrop.Payloads.PlaylistDragPayload => true,
            Wavee.UI.Services.DragDrop.Payloads.SidebarReorderPayload sp
                => !string.Equals(sp.SourceUri, folderTag, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// Walks <see cref="RootlistNode.Children"/> in arrival order, emitting playlist or
    /// folder sidebar items into <paramref name="target"/>. Stamps each row with its
    /// <see cref="SidebarItemModel.Depth"/>, which the template binds through the
    /// DepthToThicknessConverter (20 px/level) for row indentation.
    /// </summary>
    private void AppendNodeChildren(
        RootlistNode node,
        ObservableCollection<SidebarItemModel> target,
        IReadOnlyDictionary<string, PlaylistSummaryDto> playlistLookup,
        int depth)
    {
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case RootlistChildPlaylist playlist:
                    if (playlistLookup.TryGetValue(playlist.Uri, out var summary))
                    {
                        var item = CreatePlaylistSidebarItem(summary);
                        item.Depth = depth;
                        target.Add(item);
                    }
                    break;

                case RootlistChildFolder folder:
                    target.Add(BuildFolderSidebarItem(folder.Folder, playlistLookup, depth));
                    break;
            }
        }
    }

    private SidebarItemModel CreatePlaylistSidebarItem(PlaylistSummaryDto playlist)
    {
        // Capture for the DropPredicate closure — keeps the predicate stable
        // when the row is re-bound to a fresh summary later.
        var canEdit = playlist.CanEditItems;
        var item = new SidebarItemModel
        {
            Text = playlist.Name,
            IconSource = CreatePlaylistIconSource(playlist),
            Tag = playlist.Id,
            // Captured so OnPlaylistContentsChanged can gate mosaic rebuilds —
            // only mosaic-backed (null or spotify:mosaic:...) playlists need
            // re-composition on a content change.
            ImageUrl = playlist.ImageUrl,
            IsOwner = playlist.IsOwner,
            CanEditItems = canEdit,
            BadgeCount = playlist.TrackCount,
            // Playlist rows accept:
            //  - Tracks (add)             — only on rows the user can edit
            //  - Album/Artist/Liked/Show  — same edit gate (resolves to add-tracks)
            //  - PlaylistDragPayload      — same edit gate (copy source tracks)
            //  - SidebarReorderPayload    — always allowed when source != target
            //                               (handler branches on drop position:
            //                                Top/Bottom = reorder, Center = copy
            //                                — copy still respects edit gate)
            DropPredicate = payload => payload switch
            {
                Wavee.UI.Services.DragDrop.Payloads.TrackDragPayload          => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.AlbumDragPayload          => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.ArtistDragPayload         => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.LikedSongsDragPayload     => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.ShowDragPayload           => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.PlaylistDragPayload pp    => canEdit
                    && !string.Equals(pp.PlaylistUri, playlist.Id, StringComparison.OrdinalIgnoreCase),
                Wavee.UI.Services.DragDrop.Payloads.SidebarReorderPayload sp
                    => !string.Equals(sp.SourceUri, playlist.Id, StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
        };

        // Spotify "custom" playlists (auto-named, e.g. "내 플레이리스트 #15") arrive either with
        // ImageUrl == null or ImageUrl == "spotify:mosaic:id1:id2:id3:id4". Neither is loadable
        // as a single image — CreatePlaylistIconSource above seats the placeholder glyph, and
        // we attach a lazy loader so PlaylistMosaicService can compose a 2×2 bitmap and replace
        // IconSource the first time the row is realized.
        if (_mosaicService is { } service
            && (string.IsNullOrEmpty(playlist.ImageUrl) || SpotifyImageHelper.IsMosaicUri(playlist.ImageUrl)))
        {
            var playlistId = playlist.Id;
            var hint = playlist.ImageUrl;
            item.LazyIconSourceLoader = ct => service.BuildMosaicAsync(playlistId, hint, ct);
        }

        return item;
    }

    /// <summary>
    /// Reacts to a per-playlist <see cref="PlaylistChangeKind.Updated"/> event.
    /// Two-phase:
    ///   1. **Promote.** Re-query the cache. If the cached <c>ImageUrl</c> now
    ///      resolves to a real HTTPS URL (either editorial PictureSize or
    ///      user-uploaded <c>spotify:image:{hex}</c>) and the sidebar row was
    ///      seated without one, swap in a real <see cref="ImageIconSource"/>.
    ///      Covers the common case of non-owned playlists whose rootlist
    ///      decoration omits the picture — the persisted row only fills in
    ///      after the first full detail fetch, and the sidebar wouldn't
    ///      otherwise pick that up until the next sidebar refresh.
    ///   2. **Mosaic refresh.** If the cache still has no real cover (null /
    ///      <c>spotify:mosaic:</c>), invalidate + rebuild the composed mosaic.
    ///      Real-cover rows skip this step entirely.
    /// Idempotent across rapid pushes — the mosaic service's in-flight cache
    /// dedups concurrent rebuilds.
    /// </summary>
    private void OnPlaylistContentsChanged(string playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return;

        var item = FindSidebarItemByTag(playlistUri);
        if (item is null) return;

        var capturedUri = playlistUri;
        _ = Task.Run(async () =>
        {
            try
            {
                // Phase 1: real-cover promotion. Re-query the cache (cheap on a
                // hot hit) so we see whatever the latest detail fetch wrote into
                // the persisted row.
                var cached = await _playlistCache
                    .GetPlaylistAsync(capturedUri, ct: CancellationToken.None)
                    .ConfigureAwait(false);
                var httpsUrl = SpotifyImageHelper.ToHttpsUrl(cached.ImageUrl);

                if (!string.IsNullOrEmpty(httpsUrl))
                {
                    _dispatcher?.TryEnqueue(() =>
                    {
                        var current = FindSidebarItemByTag(capturedUri);
                        if (current is null) return;
                        // Skip if the URL hasn't changed AND the icon is already
                        // a loaded BitmapImage — avoids replacing a working icon
                        // and triggering needless re-decode flicker.
                        if (string.Equals(current.ImageUrl, cached.ImageUrl, StringComparison.Ordinal)
                            && current.IconSource is ImageIconSource { ImageSource: BitmapImage })
                            return;

                        current.ImageUrl = cached.ImageUrl;
                        current.IconSource = new ImageIconSource
                        {
                            ImageSource = new BitmapImage
                            {
                                UriSource = new Uri(httpsUrl),
                                DecodePixelWidth = 44
                            }
                        };
                        // No further mosaic work needed — a real cover trumps
                        // any composed placeholder. Clear the lazy loader so a
                        // subsequent realization doesn't overwrite our promotion.
                        current.LazyIconSourceLoader = null;
                    });
                    return;
                }

                // Phase 2: mosaic refresh. Cache still has no usable URL — fall
                // back to rebuilding the 2x2 composed tile from track covers.
                if (_mosaicService is null) return;
                _mosaicService.Invalidate(capturedUri);
                var icon = await _mosaicService
                    .BuildMosaicAsync(capturedUri, mosaicHint: null, CancellationToken.None)
                    .ConfigureAwait(false);
                if (icon is null) return;
                _dispatcher?.TryEnqueue(() =>
                {
                    var current = FindSidebarItemByTag(capturedUri);
                    if (current is not null)
                        current.IconSource = icon;
                });
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Sidebar cover refresh failed for {Uri}", capturedUri);
            }
        });
    }

    private static IconSource CreatePlaylistIconSource(PlaylistSummaryDto playlist)
    {
        // Route through SpotifyImageHelper so user-uploaded covers
        // (spotify:image:{hex} — what the v3 cache schema produces from
        // attributes.Picture) render alongside the editorial pre-rendered
        // HTTPS PictureSize URLs. spotify:mosaic: still returns null and
        // falls through to the lazy mosaic loader below.
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(playlist.ImageUrl);
        if (!string.IsNullOrEmpty(httpsUrl))
        {
            return new ImageIconSource
            {
                ImageSource = new BitmapImage
                {
                    UriSource = new Uri(httpsUrl),
                    DecodePixelWidth = 44
                }
            };
        }

        // ImageIconSource with null ImageSource (not FontIconSource) so
        // SidebarItem.CreateSidebarIcon routes through CreateArtworkIcon
        // and renders the same 32x32 rounded tile shape it uses for real
        // artwork. A bare FontIconSource renders at 16px, so the icon
        // rectangle would jump 16->32 when the lazy mosaic loader
        // resolves -- fade animation can't mask a size change.
        return new ImageIconSource { ImageSource = null };
    }

    private void ApplyPersistedSidebarState(SidebarItemModel item)
    {
        if (item.Tag is string tag && _shellSession.TryGetSidebarGroupExpansion(tag, out var isExpanded))
            item.IsExpanded = isExpanded;

        if (item.Children is IEnumerable<SidebarItemModel> children)
        {
            foreach (var child in children)
                ApplyPersistedSidebarState(child);
        }
    }

    private SidebarItemModel? FindSidebarItemByTag(IEnumerable<SidebarItemModel> items, string tag)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Tag, tag, StringComparison.Ordinal))
                return item;

            if (item.Children is IEnumerable<SidebarItemModel> children)
            {
                var match = FindSidebarItemByTag(children, tag);
                if (match != null)
                    return match;
            }
        }

        return null;
    }

    private void OnTabInstancesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<TabBarItem>())
                DetachTabHandlers(item);
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<TabBarItem>())
                AttachTabHandlers(item);
        }

        PersistTabSession();
    }

    private void AttachTabHandlers(TabBarItem tab)
    {
        tab.PropertyChanged += OnTrackedTabChanged;
        tab.ContentChanged += OnTrackedTabContentChanged;
    }

    private void DetachTabHandlers(TabBarItem tab)
    {
        tab.PropertyChanged -= OnTrackedTabChanged;
        tab.ContentChanged -= OnTrackedTabContentChanged;
    }

    private void OnTrackedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabBarItem.Header)
            or nameof(TabBarItem.ToolTipText)
            or nameof(TabBarItem.IsPinned)
            or nameof(TabBarItem.IsCompact)
            or nameof(TabBarItem.IconSource)
            or nameof(TabBarItem.IsSleeping))
        {
            PersistTabSession();
        }
    }

    private void OnTrackedTabContentChanged(object? sender, TabItemParameter e)
    {
        PersistTabSession();
    }

    public void PersistTabSession()
    {
        if (_restoringTabSession)
            return;

        _shellSession.SaveTabs(TabInstances, SelectedTabIndex);
    }

    public bool RestorePersistedTabs()
    {
        if (TabInstances.Count > 0)
            return true;

        var restoredTabs = _shellSession.GetRestorableTabs();
        if (restoredTabs.Count == 0)
            return false;

        _restoringTabSession = true;
        try
        {
            foreach (var tabState in restoredTabs)
            {
                var tab = NavigationHelpers.CreateTab(
                    tabState.PageType,
                    tabState.Parameter,
                    tabState.Header,
                    NavigationHelpers.CreateIconSource(tabState.PageType, tabState.Parameter),
                    tabState.IsPinned,
                    tabState.IsCompact);

                TabInstances.Add(tab);
            }

            if (TabInstances.Count == 0)
                return false;

            SelectTab(Math.Clamp(_appModel.TabStripSelectedIndex, 0, TabInstances.Count - 1));
            UpdateNavigationState();
            return true;
        }
        finally
        {
            _restoringTabSession = false;
            PersistTabSession();
        }
    }

    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        if (newValue >= 0 && newValue < TabInstances.Count)
        {
            // Set animation direction: positive = slide from right, negative = slide from left
            TabSwitchDirection = newValue > oldValue ? 1 : (newValue < oldValue ? -1 : 0);
            _previousTabIndex = oldValue;

            var nextTab = TabInstances[newValue];
            if (nextTab.IsSleeping)
                WakeTab(nextTab);
            else
                nextTab.MarkActivated();

            SelectedTabItem = nextTab;
        }

        _appModel.TabStripSelectedIndex = newValue;
        PersistTabSession();
    }

    partial void OnSelectedTabItemChanged(TabBarItem? oldValue, TabBarItem? newValue)
    {
        // Unsubscribe from previous tab
        if (oldValue != null)
        {
            oldValue.TrimActiveContentForNavigationCache();
            oldValue.Navigated -= TabItem_Navigated;
        }

        // Subscribe to new tab
        if (newValue != null)
        {
            newValue.Navigated += TabItem_Navigated;
            newValue.RestoreActiveContentFromNavigationCache();
        }

        UpdateNavigationState();
    }

    private void TabItem_Navigated(object? sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (sender is TabBarItem tab)
            tab.MarkActivated();

        UpdateNavigationState();
    }

    partial void OnSidebarWidthChanged(double value)
    {
        _appModel.SidebarWidth = value;
    }

    partial void OnSidebarDisplayModeChanged(SidebarDisplayMode value)
    {
        _appModel.SidebarDisplayMode = value;

        // The sidebar player widget can't render meaningfully in Compact (icon
        // rail) or Minimal (slide-in) modes — there's no width for it. If the
        // user collapses the sidebar while the player is docked there, auto-
        // demote it back to the bottom bar so the player stays visible.
        if (value != SidebarDisplayMode.Expanded
            && PlayerLocation == PlayerLocation.Sidebar
            && (!Docking.IsPlayerDetached
                || _settingsService?.Settings.ShowDockedPlayerWithFloatingPlayer == true))
        {
            PlayerLocation = PlayerLocation.Bottom;
        }
    }

    partial void OnIsSidebarPaneOpenChanged(bool value)
    {
        _appModel.IsSidebarPaneOpen = value;
    }

    partial void OnRightPanelWidthChanged(double value)
    {
        _appModel.RightPanelWidth = value;
    }

    partial void OnIsRightPanelOpenChanged(bool value)
    {
        _appModel.IsRightPanelOpen = value;
        WeakReferenceMessenger.Default.Send(new RightPanelStateChangedMessage(value, RightPanelMode));
        OnPropertyChanged(nameof(IsRightPanelVisibleInShell));
        OnPropertyChanged(nameof(IsFriendsPanelActive));
    }

    partial void OnRightPanelModeChanged(RightPanelMode value)
    {
        _appModel.RightPanelMode = value;
        if (IsRightPanelOpen)
            WeakReferenceMessenger.Default.Send(new RightPanelStateChangedMessage(true, value));
        OnPropertyChanged(nameof(IsFriendsPanelActive));
    }

    partial void OnPlayerLocationChanged(PlayerLocation value)
    {
        _appModel.PlayerLocation = value;

        // Moving the player INTO the sidebar — make sure the sidebar is in a
        // mode that can host it. Compact rail and Minimal flyout don't have
        // room. Auto-promote to Expanded; the existing SidebarWidth setting
        // is the "last known width" and the visual states use it via
        // OpenPaneLength → PaneColumnDefinition.Width.
        if (value == PlayerLocation.Sidebar && SidebarDisplayMode != SidebarDisplayMode.Expanded)
        {
            SidebarDisplayMode = SidebarDisplayMode.Expanded;
        }

        OnPropertyChanged(nameof(IsSidebarPlayerVisibleInShell));
        OnPropertyChanged(nameof(IsBottomPlayerVisibleInShell));
    }

    partial void OnSidebarPlayerCollapsedChanged(bool value)
    {
        _appModel.SidebarPlayerCollapsed = value;
    }

    [RelayCommand]
    private void TogglePlayerLocation()
    {
        PlayerLocation = PlayerLocation == PlayerLocation.Bottom
            ? PlayerLocation.Sidebar
            : PlayerLocation.Bottom;
    }

    /// <summary>
    /// Open the now-playing surface: ensure the sidebar player widget is
    /// visible (PlayerLocation = Sidebar) AND expanded (SidebarPlayerCollapsed = false).
    /// Idempotent — a second call when already open does nothing because the
    /// generated property setters short-circuit on equal values.
    ///
    /// Wired to the bottom PlayerBar's track-title click so the user always has
    /// a discoverable path back to the now-playing surface — including videos,
    /// where SidebarPlayerWidget renders the active video surface in
    /// ExpandedVideoHost.
    /// </summary>
    [RelayCommand]
    private void OpenNowPlaying()
    {
        if (PlayerLocation != PlayerLocation.Sidebar)
            PlayerLocation = PlayerLocation.Sidebar;
        if (SidebarPlayerCollapsed)
            SidebarPlayerCollapsed = false;
    }

    private void ToggleRightPanel(RightPanelMode mode)
    {
        if (IsRightPanelOpen && RightPanelMode == mode)
        {
            IsRightPanelOpen = false;
            if (mode == RightPanelMode.TrackDetails)
                SelectedTrackForDetails = null;
        }
        else
        {
            RightPanelMode = mode;
            IsRightPanelOpen = true;
        }
    }

    /// <summary>
    /// Selected <see cref="ITrackItem"/> feeding the <see cref="RightPanelMode.TrackDetails"/>
    /// tab. Set via <see cref="ShowTrackDetails"/> when a TrackDataGrid row's details button
    /// fires; cleared when the panel is toggled off for that mode.
    /// </summary>
    [ObservableProperty]
    private Wavee.UI.WinUI.Data.Contracts.ITrackItem? _selectedTrackForDetails;

    /// <summary>
    /// Open the right panel with the temporary "Track details" tab showing metadata for
    /// <paramref name="track"/>. No-op when <paramref name="track"/> is null.
    /// </summary>
    public void ShowTrackDetails(Wavee.UI.WinUI.Data.Contracts.ITrackItem? track)
    {
        if (track is null) return;
        SelectedTrackForDetails = track;
        RightPanelMode = RightPanelMode.TrackDetails;
        IsRightPanelOpen = true;
    }

    partial void OnSelectedSidebarItemChanged(ISidebarItemModel? value)
    {
        // Navigation is handled in ShellPage.SidebarControl_ItemInvoked
        // to support modifier keys (Ctrl/middle-click for new tab)
        _shellSession.UpdateSelectedSidebarTag((value as SidebarItemModel)?.Tag);
        UpdateAliasSelections(value);
    }

    /// <summary>
    /// Walks the sidebar tree and toggles <see cref="SidebarItemModel.IsAliasSelected"/>
    /// on rows that aren't the primary selection but represent the same logical
    /// destination — e.g. when the pinned <c>spotify:collection</c> row is selected,
    /// the Your-Library "Liked Songs" row also lights up. Without this, only one of
    /// the two surfaces shows the selected indicator even though both point at the
    /// same page.
    /// </summary>
    private void UpdateAliasSelections(ISidebarItemModel? selected)
    {
        var selectedTag = (selected as SidebarItemModel)?.Tag;
        ApplyAliasSelections(SidebarItems, selected, selectedTag);
    }

    private static void ApplyAliasSelections(
        IEnumerable<SidebarItemModel> items,
        ISidebarItemModel? selected,
        string? selectedTag)
    {
        foreach (var item in items)
        {
            var isAlias = !ReferenceEquals(item, selected)
                && selectedTag is { Length: > 0 }
                && !string.IsNullOrEmpty(item.Tag)
                && AreEquivalentSidebarTags(selectedTag, item.Tag!);

            if (item.IsAliasSelected != isAlias)
                item.IsAliasSelected = isAlias;

            if (item.Children is IEnumerable<SidebarItemModel> children)
                ApplyAliasSelections(children, selected, selectedTag);
        }
    }

    private static bool AreEquivalentSidebarTags(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return false;
        return IsAliasOf(a, b) || IsAliasOf(b, a);
    }

    private static bool IsAliasOf(string canonical, string candidate)
    {
        return canonical switch
        {
            "LikedSongs" =>
                candidate == "spotify:collection"
                || (candidate.StartsWith("spotify:user:", StringComparison.Ordinal)
                    && candidate.EndsWith(":collection", StringComparison.Ordinal)),
            "Podcasts" =>
                candidate == "spotify:collection:your-episodes",
            _ => false
        };
    }

    public ElementTheme CurrentTheme => _themeService.CurrentTheme;

    [RelayCommand]
    private void ToggleTheme()
    {
        _themeService.ToggleTheme();
        OnPropertyChanged(nameof(CurrentTheme));
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // TODO: Navigate to settings
    }

    [RelayCommand]
    private void CloseTab(TabBarItem? tab)
    {
        if (tab is null) return;

        var index = TabInstances.IndexOf(tab);
        if (index < 0) return;

        TabInstances.RemoveAt(index);
        tab.Dispose();

        if (TabInstances.Count == 0)
        {
            // Open home if no tabs left
            Helpers.Navigation.NavigationHelpers.OpenHome();
        }
        else if (SelectedTabIndex >= TabInstances.Count)
        {
            SelectedTabIndex = TabInstances.Count - 1;
        }

        // Tab close is a deliberate user action where a brief stutter is acceptable
        // in exchange for actually returning the closed page's visual tree, composition
        // resources, and view-model state to the OS. Without this the .NET runtime and
        // DirectComposition both lazy-release and the working set stays elevated until
        // the next gen2 collection many seconds later.
        Services.MemoryReleaseHelper.ReleaseWorkingSet(_logger, "tab-close");
    }

    public void ToggleTabSleep(TabBarItem? tab)
    {
        if (tab == null)
            return;

        if (tab.IsSleeping)
        {
            WakeTab(tab);
            return;
        }

        SleepTab(tab);
    }

    public void SleepTab(TabBarItem? tab)
    {
        if (tab == null)
            return;

        if (ReferenceEquals(tab, SelectedTabItem))
            return;

        if (!tab.Sleep())
            return;

        PersistTabSession();
        MaybeReleaseMemoryAfterTabSleep("tab-sleep");
    }

    public void WakeTab(TabBarItem? tab)
    {
        if (tab == null)
            return;

        if (!tab.Wake())
            return;

        PersistTabSession();
        UpdateNavigationState();
    }

    public void GoBack()
    {
        if (SelectedTabItem?.ContentFrame is Frame frame && frame.CanGoBack)
        {
            // CONNECTED-ANIM (disabled): suppression of default transition is only
            // meaningful when connected animations are running. With them disabled,
            // every back navigation uses the default Slide transition.
            // var currentPage = frame.Content;
            // var isContentPage = currentPage is Views.ArtistPage
            //                  or Views.AlbumPage
            //                  or Views.PlaylistPage;
            //
            // if (isContentPage)
            //     frame.GoBack(new SuppressNavigationTransitionInfo());
            // else
            //     frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
            frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });

            UpdateNavigationState();
        }
    }

    public void GoForward()
    {
        if (SelectedTabItem?.ContentFrame is Frame frame && frame.CanGoForward)
        {
            // Note: Frame.GoForward() doesn't support transition info parameter in WinUI 3
            // The page's built-in transition will be used instead
            frame.GoForward();
            UpdateNavigationState();
        }
    }

    [ObservableProperty]
    private List<SearchSuggestionItem>? _searchSuggestions;

    /// <summary>
    /// Grouped suggestions for the three-section omnibar mode (Settings / Your library
    /// / Spotify). When this contains any items the Omnibar prefers grouped rendering
    /// over the flat <see cref="SearchSuggestions"/> list. Null/empty falls back to
    /// the legacy flat path used by recent searches and no-match fallback.
    /// </summary>
    [ObservableProperty]
    private List<SearchSuggestionGroup>? _suggestionGroups;

    [ObservableProperty]
    private bool _isSearchSuggestionsLoading;

    [ObservableProperty]
    private string? _searchSuggestionErrorMessage;

    private sealed record CachedSearchSuggestions(List<SearchSuggestionItem> Items, DateTimeOffset CachedAt);

    public void Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // URL / URI paste — navigate straight to the entity instead of searching for
        // the literal URL on the SearchPage. Uses whatever placeholder data we have;
        // destination pages prefill their hero from the URI.
        if (SpotifyLink.TryParse(query.Trim(), out var link))
        {
            _linkPreviewCts?.Cancel();
            ClearSearchSuggestionState();
            NavigateToLink(link, title: null, imageUrl: null);
            return;
        }

        InvalidateRecentSearchesCache();

        // Navigate to search page with query
        Helpers.Navigation.NavigationHelpers.OpenSearch(query);
    }

    public async void OnSearchTextChanged(string text)
    {
        var normalizedText = text?.Trim() ?? string.Empty;
        _activeSearchText = normalizedText;

        try
        {
            // Fast path: Spotify URL / URI paste. Replaces the normal three-section
            // search with a single "Open link" suggestion that previews the entity.
            // Skip-ahead works regardless of current page (including SearchPage) — we
            // don't want to re-search for the literal URL.
            if (!string.IsNullOrWhiteSpace(normalizedText)
                && SpotifyLink.TryParse(normalizedText, out var link))
            {
                ApplyLinkPasteSuggestion(link, normalizedText);
                return;
            }

            // Text is not a link: drop any in-flight preview so a late-arriving result
            // can't overwrite the now-valid search suggestions.
            _linkPreviewCts?.Cancel();

            // If already on SearchPage, re-search directly instead of showing suggestions
            if (SelectedTabItem?.ContentFrame?.Content is SearchPage searchPage
                && !string.IsNullOrWhiteSpace(normalizedText))
            {
                ClearSearchSuggestionState();
                await _searchDebouncer.DebounceAsync(async _ =>
                {
                    await searchPage.ViewModel.LoadAsync(normalizedText);
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                // Empty → hide sectioned groups, show recent searches via flat list.
                _searchDebouncer.Cancel();
                SearchSuggestionErrorMessage = null;
                SuggestionGroups = null;

                if (TryGetCachedRecentSearches(out var cachedRecents, out var recentCacheIsFresh))
                {
                    SearchSuggestions = cachedRecents;
                    IsSearchSuggestionsLoading = false;
                    if (recentCacheIsFresh)
                        return;

                    _ = RefreshRecentSearchesSafeAsync(normalizedText);
                    return;
                }

                SearchSuggestions = null;
                IsSearchSuggestionsLoading = true;
                await RefreshRecentSearchesAsync(normalizedText);
            }
            else
            {
                SearchSuggestionErrorMessage = null;
                SearchSuggestions = null; // flat list is off when sectioned mode is active
                IsSearchSuggestionsLoading = true;

                // 1) Synchronous Settings filter — always ≤ 3 items, in-memory.
                var settingsItems = BuildSettingsSuggestions(normalizedText);

                // 2) Zero-network library quicksearch — broadened to AllCached so anything
                //    the user has seen/played is findable, not just explicitly-saved items.
                var libraryItems = await BuildLibrarySuggestionsAsync(normalizedText, CancellationToken.None);

                if (!string.Equals(_activeSearchText, normalizedText, StringComparison.Ordinal))
                    return;

                // 3) Try cached Spotify suggestions; show partial groups immediately when missing.
                List<SearchSuggestionItem>? spotifyItems = null;
                var queryCacheIsFresh = false;
                if (TryGetCachedQuerySuggestions(normalizedText, out var cachedSpotify, out queryCacheIsFresh))
                {
                    spotifyItems = cachedSpotify;
                }

                SuggestionGroups = BuildGroups(settingsItems, libraryItems, spotifyItems);
                IsSearchSuggestionsLoading = spotifyItems is null;

                if (spotifyItems is not null && queryCacheIsFresh)
                    return;

                // 4) Debounce 300ms then refresh the Spotify group.
                await _searchDebouncer.DebounceAsync(async ct =>
                {
                    await RefreshQuerySuggestionsAsync(normalizedText, ct);
                });
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[Shell] Search suggestion query cancelled for \"{Query}\"", normalizedText);
        }
        catch (Exception ex)
        {
            ApplySearchSuggestionFailure(normalizedText, ex);
        }
    }

    /// <summary>Maps the static SettingsPage entries through the omnibar query, capped at 3.</summary>
    private static List<SearchSuggestionItem> BuildSettingsSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchSuggestionItem>();

        return Wavee.UI.WinUI.Views.SettingsPage.SettingsSearchEntries
            .Where(entry => entry.Matches(query))
            .Take(3)
            .Select(entry => new SearchSuggestionItem
            {
                Title = entry.Title,
                Subtitle = entry.Section,
                Uri = $"wavee:setting:{entry.Tag}:{entry.GroupKey}",
                Type = SearchSuggestionType.Setting,
                ContextTag = entry.Tag,
                GroupKey = entry.GroupKey,
                QueryText = query,
            })
            .ToList();
    }

    /// <summary>
    /// Calls <see cref="Wavee.Local.ILocalLibraryService.SearchAsync"/> across all
    /// cached entities (local files + cached Spotify tracks/albums/artists/playlists). Maps
    /// each result to a Local* suggestion type so the dispatcher knows whether to play (Track),
    /// open Album/Artist/Playlist pages, etc.
    /// </summary>
    private async Task<List<SearchSuggestionItem>> BuildLibrarySuggestionsAsync(string query, CancellationToken ct)
    {
        if (_localLibrary is null || string.IsNullOrWhiteSpace(query))
            return new List<SearchSuggestionItem>();

        IReadOnlyList<Wavee.Local.LocalSearchResult> results;
        try
        {
            results = await _localLibrary.SearchAsync(
                query,
                limit: 8,
                Wavee.Local.LocalSearchScope.AllCached,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Local library quicksearch failed for \"{Query}\"", query);
            return new List<SearchSuggestionItem>();
        }

        var items = new List<SearchSuggestionItem>(results.Count);
        foreach (var r in results)
        {
            var subtitle = string.IsNullOrWhiteSpace(r.Subtitle)
                ? "Your library"
                : "Your library · " + r.Subtitle;

            items.Add(new SearchSuggestionItem
            {
                Title = r.Name,
                Subtitle = subtitle,
                ImageUrl = r.ArtworkUri,
                Uri = r.Uri,
                Type = r.Type switch
                {
                    Wavee.Local.LocalSearchEntityType.Track    => SearchSuggestionType.LocalTrack,
                    Wavee.Local.LocalSearchEntityType.Album    => SearchSuggestionType.LocalAlbum,
                    Wavee.Local.LocalSearchEntityType.Artist   => SearchSuggestionType.LocalArtist,
                    Wavee.Local.LocalSearchEntityType.Playlist => SearchSuggestionType.LocalPlaylist,
                    _ => SearchSuggestionType.LocalTrack,
                },
                QueryText = query,
            });
        }
        return items;
    }

    /// <summary>
    /// Composes the three sections into a flat list of groups. Empty Settings/Library
    /// sections are dropped. The Spotify section behavior depends on its argument:
    ///   - <c>spotify == null</c> → pending state, show shimmer placeholders so the
    ///     section is visible from frame 1 instead of popping in 300 ms later.
    ///   - <c>spotify.Count == 0</c> → network responded with no matches, drop section.
    ///   - <c>spotify.Count &gt; 0</c> → real items.
    /// </summary>
    private static List<SearchSuggestionGroup> BuildGroups(
        List<SearchSuggestionItem> settings,
        List<SearchSuggestionItem> library,
        List<SearchSuggestionItem>? spotify)
    {
        var groups = new List<SearchSuggestionGroup>(3);
        if (settings.Count > 0)
            groups.Add(new SearchSuggestionGroup("SETTINGS", settings));
        if (library.Count > 0)
            groups.Add(new SearchSuggestionGroup("YOUR LIBRARY", library));

        if (spotify is null)
            groups.Add(new SearchSuggestionGroup("SPOTIFY", SpotifyShimmerPlaceholders));
        else if (spotify.Count > 0)
            groups.Add(new SearchSuggestionGroup("SPOTIFY", spotify));

        return groups;
    }


    public void RetrySearchSuggestions()
    {
        OnSearchTextChanged(_activeSearchText);
    }

    public void OnSuggestionChosen(object? item)
    {
        if (item is not SearchSuggestionItem suggestion) return;
        if (suggestion.Type == SearchSuggestionType.SectionHeader) return; // defense-in-depth
        if (suggestion.Type == SearchSuggestionType.Shimmer) return;       // placeholder is non-interactive

        InvalidateRecentSearchesCache();

        switch (suggestion.Type)
        {
            case SearchSuggestionType.Artist:
                NavigationHelpers.OpenArtist(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.Album:
                NavigationHelpers.OpenAlbum(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.Playlist:
                NavigationHelpers.OpenPlaylist(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.Track:
                var trackId = suggestion.Uri.Replace("spotify:track:", "");
                _playbackStateService.PlayTrack(trackId);
                break;
            case SearchSuggestionType.TextQuery:
                var query = suggestion.Uri.Replace("spotify:search:", "").Replace("+", " ");
                NavigationHelpers.OpenSearch(query);
                break;

            // Omnibar link-paste destinations (entity types not produced by free-text search).
            case SearchSuggestionType.Podcast:
                NavigationHelpers.OpenShowPage(suggestion.Uri, suggestion.Title, subtitle: null, imageUrl: suggestion.ImageUrl);
                break;
            case SearchSuggestionType.Episode:
                NavigationHelpers.OpenEpisodePage(suggestion.Uri, suggestion.Title, suggestion.ImageUrl);
                break;
            case SearchSuggestionType.User:
                NavigationHelpers.OpenProfile(new ContentNavigationParameter
                {
                    Uri = suggestion.Uri,
                    Title = suggestion.Title,
                    ImageUrl = suggestion.ImageUrl,
                }, suggestion.Title);
                break;
            case SearchSuggestionType.Genre:
                NavigationHelpers.OpenBrowsePage(new ContentNavigationParameter
                {
                    Uri = suggestion.Uri,
                    Title = string.IsNullOrWhiteSpace(suggestion.Title) ? "Browse" : suggestion.Title,
                    ImageUrl = suggestion.ImageUrl,
                });
                break;
            case SearchSuggestionType.LinkAction:
                if (suggestion.Uri == "spotify:collection")
                    NavigationHelpers.OpenLikedSongs();
                else if (suggestion.Uri == "spotify:collection:your-episodes")
                    NavigationHelpers.OpenYourEpisodes();
                break;

            // Omnibar Settings deep-link — reuse the in-page filter via existing
            // NavigateToSearchEntry path on SettingsPage.OnNavigatedTo.
            case SearchSuggestionType.Setting:
                if (!string.IsNullOrEmpty(suggestion.ContextTag) && !string.IsNullOrEmpty(suggestion.GroupKey))
                {
                    NavigationHelpers.OpenSettings(new Wavee.UI.WinUI.Data.Parameters.SettingsNavigationParameter(
                        suggestion.ContextTag, suggestion.GroupKey, suggestion.Title));
                }
                else
                {
                    NavigationHelpers.OpenSettings();
                }
                break;

            // Your-library quicksearch results. URIs are either wavee:local:... (filesystem)
            // or spotify:... (cached Spotify saved items). The existing NavigationHelpers
            // already handle local URIs via the SearchPage merge path, so the same helpers
            // work for both.
            case SearchSuggestionType.LocalTrack:
                _playbackStateService.PlayTrack(suggestion.Uri);
                break;
            case SearchSuggestionType.LocalAlbum:
                NavigationHelpers.OpenAlbum(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.LocalArtist:
                NavigationHelpers.OpenArtist(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.LocalPlaylist:
                NavigationHelpers.OpenPlaylist(suggestion.Uri, suggestion.Title);
                break;

            default:
                NavigationHelpers.OpenSearch(suggestion.Title);
                break;
        }
    }

    public void OnSuggestionActionClicked(SearchSuggestionItem item)
    {
        switch (item.Type)
        {
            case SearchSuggestionType.Track:
                var trackId = item.Uri.Replace("spotify:track:", "");
                _playbackStateService.AddToQueue(trackId);
                break;
        }
    }

    [ObservableProperty]
    private bool _isOnSearchPage;

    /// <summary>
    /// True when the active tab is hosting <see cref="Wavee.UI.WinUI.Views.VideoPlayerPage"/>.
    /// Drives both the bottom-bar suppression (the page owns the transport, no point
    /// in duplicating it) and the floating mini-player suppression (the page already
    /// owns the video surface). Single source of truth — replaces the old per-page
    /// SetOnVideoPage flip in VideoPlayerPage.OnNavigatedTo/From which double-fired
    /// when the same page type was also hosted in the Theatre overlay frame.
    /// </summary>
    [ObservableProperty]
    private bool _isOnVideoPage;

    partial void OnIsOnVideoPageChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBottomPlayerVisibleInShell));
        OnPropertyChanged(nameof(IsMiniPlayerVisibleInShell));
    }

    public void UpdateNavigationState()
    {
        bool onVideo = false;
        if (SelectedTabItem?.ContentFrame is Frame frame)
        {
            CanGoBack = frame.CanGoBack;
            CanGoForward = frame.CanGoForward;
            IsOnHomePage = frame.Content is HomePage;
            IsOnProfilePage = frame.Content is ProfilePage;
            IsOnSearchPage = frame.Content is SearchPage;
            onVideo = frame.Content is Wavee.UI.WinUI.Views.VideoPlayerPage;

            if (frame.Content?.GetType() is { } contentType
                && NavigationHelpers.GetLocalSidebarTag(contentType) is { } localSidebarTag)
            {
                SyncSidebarSelectionToTag(localSidebarTag);
            }
        }
        else
        {
            CanGoBack = false;
            CanGoForward = false;
            IsOnHomePage = false;
            IsOnProfilePage = false;
            IsOnSearchPage = false;
        }

        // Push the canonical "is on video page" signal. Sets the observable
        // property that the bottom bar + mini-player visibility helpers
        // depend on, AND keeps the legacy mini-VM SetOnVideoPage forward
        // working for any subscribers that still listen there.
        IsOnVideoPage = onVideo;
        try
        {
            var miniVm = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Wavee.UI.WinUI.ViewModels.MiniVideoPlayerViewModel>();
            miniVm?.SetOnVideoPage(onVideo);
        }
        catch
        {
            // Mini VM might not be registered yet during early startup.
        }
    }

    /// <summary>
    /// Cleans up event subscriptions to prevent memory leaks.
    /// </summary>
    public void Cleanup()
    {
        if (_tabSleepTimer != null)
        {
            _tabSleepTimer.Stop();
            _tabSleepTimer.Tick -= TabSleepTimer_Tick;
            _tabSleepTimer = null;
        }

        _searchDebouncer.Dispose();
        _linkPreviewCts?.Cancel();
        _linkPreviewCts?.Dispose();
        _linkPreviewCts = null;
        _libraryDataService.PlaylistsChanged -= OnPlaylistsChanged;
        _libraryDataService.DataChanged -= OnLibraryDataChanged;
        _playlistMosaicChangesSubscription?.Dispose();
        _playlistMosaicChangesSubscription = null;

        // Cancel any in-flight debounced playlist refresh so we don't touch disposed state.
        var pending = Interlocked.Exchange(ref _playlistRefreshCts, null);
        pending?.Cancel();
        pending?.Dispose();
        _notificationService.PropertyChanged -= OnNotificationServicePropertyChanged;
        // Match the 5 Register<T> calls in the constructor — the 4 beyond
        // ToggleRightPanelMessage were omitted, leaving each handler closure
        // pinning the ShellViewModel (captured `this`) against GC. Although
        // ShellViewModel is effectively a singleton per session so the leak
        // is bounded, the closure chain also roots ILibraryDataService +
        // DispatcherQueue references, which matters if the VM is ever
        // reconstructed (e.g. on sign-out / sign-in cycles).
        WeakReferenceMessenger.Default.Unregister<ToggleRightPanelMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.LibrarySyncStartedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.LibrarySyncFailedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.LibrarySyncCompletedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        TabInstances.CollectionChanged -= OnTabInstancesCollectionChanged;

        foreach (var tab in TabInstances)
            DetachTabHandlers(tab);

        foreach (var group in SidebarItems)
            group.PropertyChanged -= OnSidebarGroupPropertyChanged;

        // Cleanup sidebar button handlers
        if (_playlistsAddButton != null)
        {
            _playlistsAddButton = null;
        }

        if (_newPlaylistMenuItem != null)
        {
            _newPlaylistMenuItem.Click -= NewPlaylistMenuItem_Click;
            _newPlaylistMenuItem = null;
        }

        if (_newFolderMenuItem != null)
        {
            _newFolderMenuItem.Click -= NewFolderMenuItem_Click;
            _newFolderMenuItem = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Cleanup();

    private void InitializeTabSleepTimer()
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue == null)
            return;

        _tabSleepTimer = dispatcherQueue.CreateTimer();
        _tabSleepTimer.Interval = TabSleepEvaluationInterval;
        _tabSleepTimer.IsRepeating = true;
        _tabSleepTimer.Tick += TabSleepTimer_Tick;
        _tabSleepTimer.Start();
    }

    private void TabSleepTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var now = DateTimeOffset.UtcNow;
        var sleptAnyTabs = false;

        for (var i = 0; i < TabInstances.Count; i++)
        {
            var tab = TabInstances[i];
            if (ReferenceEquals(tab, SelectedTabItem) || tab.IsPinned || tab.IsSleeping)
                continue;

            if (now - tab.LastActivatedAtUtc < TabSleepTimeout)
                continue;

            if (tab.Sleep())
                sleptAnyTabs = true;
        }

        if (!sleptAnyTabs)
            return;

        PersistTabSession();
        MaybeReleaseMemoryAfterTabSleep("auto-tab-sleep");
    }

    private void MaybeReleaseMemoryAfterTabSleep(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastTabSleepMemoryReleaseUtc < TabSleepMemoryReleaseThrottle)
            return;

        _lastTabSleepMemoryReleaseUtc = now;
        Services.MemoryReleaseHelper.ReleaseWorkingSet(_logger, reason);
    }

    private bool TryGetCachedRecentSearches(out List<SearchSuggestionItem> items, out bool isFresh)
    {
        if (_recentSearchesCache != null)
        {
            items = CloneSuggestions(_recentSearchesCache.Items);
            isFresh = DateTimeOffset.UtcNow - _recentSearchesCache.CachedAt <= RecentSearchesCacheLifetime;
            return true;
        }

        items = [];
        isFresh = false;
        return false;
    }

    private bool TryGetCachedQuerySuggestions(string query, out List<SearchSuggestionItem> items, out bool isFresh)
    {
        if (_querySuggestionCache.TryGetValue(query, out var cached))
        {
            items = CloneSuggestions(cached.Items);
            isFresh = DateTimeOffset.UtcNow - cached.CachedAt <= QuerySuggestionsCacheLifetime;
            return true;
        }

        items = [];
        isFresh = false;
        return false;
    }

    private async Task RefreshRecentSearchesAsync(string querySnapshot, CancellationToken ct = default)
    {
        var recents = await _searchService.GetRecentSearchesAsync(ct);
        _recentSearchesCache = new CachedSearchSuggestions(CloneSuggestions(recents), DateTimeOffset.UtcNow);

        if (string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
        {
            SearchSuggestionErrorMessage = null;
            IsSearchSuggestionsLoading = false;
            SearchSuggestions = CloneSuggestions(recents);
        }
    }

    private async Task RefreshQuerySuggestionsAsync(string querySnapshot, CancellationToken ct)
    {
        // Network leg: cache Spotify suggestions keyed by query (existing pattern).
        var spotifyItems = await _searchService.GetSuggestionsAsync(querySnapshot, ct);
        StoreQuerySuggestionCache(querySnapshot, spotifyItems);

        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        // Recompute the other two sections so they stay in sync with the current query.
        var settingsItems = BuildSettingsSuggestions(querySnapshot);
        var libraryItems = await BuildLibrarySuggestionsAsync(querySnapshot, ct);

        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        SearchSuggestionErrorMessage = null;
        IsSearchSuggestionsLoading = false;
        SuggestionGroups = BuildGroups(settingsItems, libraryItems, CloneSuggestions(spotifyItems));
    }

    private async Task RefreshRecentSearchesSafeAsync(string querySnapshot)
    {
        try
        {
            await RefreshRecentSearchesAsync(querySnapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ApplySearchSuggestionFailure(querySnapshot, ex);
        }
    }

    private void StoreQuerySuggestionCache(string query, List<SearchSuggestionItem> suggestions)
    {
        _querySuggestionCache[query] = new CachedSearchSuggestions(CloneSuggestions(suggestions), DateTimeOffset.UtcNow);

        if (_querySuggestionCache.Count <= MaxCachedSuggestionQueries)
            return;

        var oldest = _querySuggestionCache
            .OrderBy(kvp => kvp.Value.CachedAt)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(oldest.Key))
            _querySuggestionCache.Remove(oldest.Key);
    }

    private void InvalidateRecentSearchesCache()
    {
        _recentSearchesCache = null;
    }

    private void ClearSearchSuggestionState()
    {
        _searchDebouncer.Cancel();
        SearchSuggestionErrorMessage = null;
        IsSearchSuggestionsLoading = false;
        SearchSuggestions = null;
        SuggestionGroups = null;
    }

    // ── Spotify URL / URI paste handling (omnibar fast path) ──────────────────────────

    /// <summary>
    /// Replaces the omnibar suggestions with a single "Open link" card for the parsed
    /// Spotify URL / URI, and kicks off an async metadata fetch to fill in the real
    /// title and cover art.
    /// </summary>
    private void ApplyLinkPasteSuggestion(SpotifyLink link, string rawText)
    {
        _searchDebouncer.Cancel();
        _linkPreviewCts?.Cancel();
        _linkPreviewCts?.Dispose();
        _linkPreviewCts = new CancellationTokenSource();

        SearchSuggestionErrorMessage = null;
        SearchSuggestions = null;
        IsSearchSuggestionsLoading = false;

        SuggestionGroups = new List<SearchSuggestionGroup>
        {
            new("OPEN LINK", new List<SearchSuggestionItem>
            {
                BuildLinkSuggestion(link, rawText, preview: null),
            }),
        };

        if (_linkPreviewService is not null)
            _ = ResolveLinkPreviewAsync(link, rawText, _linkPreviewCts.Token);
    }

    private async Task ResolveLinkPreviewAsync(SpotifyLink link, string rawText, CancellationToken ct)
    {
        if (_linkPreviewService is null) return;
        LinkPreview? preview;
        try
        {
            preview = await _linkPreviewService.ResolveAsync(link, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Bail out if the user typed past this URL while the fetch was in flight.
        if (ct.IsCancellationRequested) return;
        if (!string.Equals(_activeSearchText, rawText, StringComparison.Ordinal)) return;
        if (preview is null) return;

        SuggestionGroups = new List<SearchSuggestionGroup>
        {
            new("OPEN LINK", new List<SearchSuggestionItem>
            {
                BuildLinkSuggestion(link, rawText, preview),
            }),
        };
    }

    private static SearchSuggestionItem BuildLinkSuggestion(SpotifyLink link, string rawText, LinkPreview? preview)
    {
        var (placeholderTitle, placeholderSubtitle) = GetLinkPlaceholder(link.Kind);

        return new SearchSuggestionItem
        {
            Title = preview?.Title ?? placeholderTitle,
            Subtitle = preview?.Subtitle ?? placeholderSubtitle ?? TrimLinkForDisplay(rawText),
            ImageUrl = preview?.ImageUrl,
            Uri = link.CanonicalUri,
            Type = MapLinkKindToSuggestionType(link.Kind),
            QueryText = rawText,
        };
    }

    private static (string Title, string? Subtitle) GetLinkPlaceholder(SpotifyLinkKind kind) => kind switch
    {
        SpotifyLinkKind.Track        => ("Open track", "Track"),
        SpotifyLinkKind.Album        => ("Open album", "Album"),
        SpotifyLinkKind.Artist       => ("Open artist", "Artist"),
        SpotifyLinkKind.Playlist     => ("Open playlist", "Playlist"),
        SpotifyLinkKind.Show         => ("Open podcast", "Podcast"),
        SpotifyLinkKind.Episode      => ("Open episode", "Episode"),
        SpotifyLinkKind.User         => ("Open profile", "Profile"),
        SpotifyLinkKind.LikedSongs   => ("Liked Songs", "Playlist"),
        SpotifyLinkKind.YourEpisodes => ("Your Episodes", "Podcasts"),
        SpotifyLinkKind.Genre        => ("Open browse page", null),
        _                            => ("Open link", null),
    };

    private static SearchSuggestionType MapLinkKindToSuggestionType(SpotifyLinkKind kind) => kind switch
    {
        SpotifyLinkKind.Track        => SearchSuggestionType.Track,
        SpotifyLinkKind.Album        => SearchSuggestionType.Album,
        SpotifyLinkKind.Artist       => SearchSuggestionType.Artist,
        SpotifyLinkKind.Playlist     => SearchSuggestionType.Playlist,
        SpotifyLinkKind.Show         => SearchSuggestionType.Podcast,
        SpotifyLinkKind.Episode      => SearchSuggestionType.Episode,
        SpotifyLinkKind.User         => SearchSuggestionType.User,
        SpotifyLinkKind.Genre        => SearchSuggestionType.Genre,
        SpotifyLinkKind.LikedSongs   => SearchSuggestionType.LinkAction,
        SpotifyLinkKind.YourEpisodes => SearchSuggestionType.LinkAction,
        _                            => SearchSuggestionType.TextQuery,
    };

    private static string TrimLinkForDisplay(string raw)
    {
        const int max = 64;
        return raw.Length <= max ? raw : string.Concat(raw.AsSpan(0, max - 1), "…");
    }

    /// <summary>
    /// Direct-navigation path used when the user presses Enter without a suggestion
    /// selected. Mirrors the dispatch in <see cref="OnSuggestionChosen"/> but takes
    /// raw link data instead of a built suggestion.
    /// </summary>
    private void NavigateToLink(SpotifyLink link, string? title, string? imageUrl)
    {
        switch (link.Kind)
        {
            case SpotifyLinkKind.Track:
                _playbackStateService.PlayTrack(link.EntityId ?? string.Empty);
                break;
            case SpotifyLinkKind.Album:
                NavigationHelpers.OpenAlbum(link.CanonicalUri, title ?? "Album");
                break;
            case SpotifyLinkKind.Artist:
                NavigationHelpers.OpenArtist(link.CanonicalUri, title ?? "Artist");
                break;
            case SpotifyLinkKind.Playlist:
                NavigationHelpers.OpenPlaylist(link.CanonicalUri, title ?? "Playlist");
                break;
            case SpotifyLinkKind.Show:
                NavigationHelpers.OpenShowPage(link.CanonicalUri, title, subtitle: null, imageUrl: imageUrl);
                break;
            case SpotifyLinkKind.Episode:
                NavigationHelpers.OpenEpisodePage(link.CanonicalUri, title, imageUrl);
                break;
            case SpotifyLinkKind.User:
                NavigationHelpers.OpenProfile(new ContentNavigationParameter
                {
                    Uri = link.CanonicalUri,
                    Title = title,
                    ImageUrl = imageUrl,
                }, title);
                break;
            case SpotifyLinkKind.LikedSongs:
                NavigationHelpers.OpenLikedSongs();
                break;
            case SpotifyLinkKind.YourEpisodes:
                NavigationHelpers.OpenYourEpisodes();
                break;
            case SpotifyLinkKind.Genre:
                NavigationHelpers.OpenBrowsePage(new ContentNavigationParameter
                {
                    Uri = link.CanonicalUri,
                    Title = title ?? "Browse",
                    ImageUrl = imageUrl,
                });
                break;
        }
    }

    private void ApplySearchSuggestionFailure(string querySnapshot, Exception ex)
    {
        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        _logger?.LogWarning(ex, "Failed to fetch search suggestions");
        IsSearchSuggestionsLoading = false;

        // Spotify leg failed. Strip its shimmer placeholders so the section doesn't keep
        // pulsing forever. Keep partial Settings + Library groups visible — the user still
        // gets local results.
        if (SuggestionGroups is { Count: > 0 } current)
        {
            var trimmed = current.Where(g => g.Count == 0 || g[0].Type != SearchSuggestionType.Shimmer).ToList();
            if (trimmed.Count > 0)
            {
                SuggestionGroups = trimmed;
                return;
            }
        }

        if (SearchSuggestions is { Count: > 0 } currentFlat
            && DoSuggestionsMatchQuery(currentFlat, querySnapshot))
        {
            return;
        }

        SearchSuggestions = null;
        SuggestionGroups = null;
        SearchSuggestionErrorMessage = ErrorMapper.ToUserMessage(ex);
    }

    private static bool DoSuggestionsMatchQuery(IReadOnlyList<SearchSuggestionItem> items, string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return items.All(item => string.IsNullOrWhiteSpace(item.QueryText));

        return items.All(item =>
            string.Equals(item.QueryText, queryText, StringComparison.OrdinalIgnoreCase));
    }

    private static List<SearchSuggestionItem> CloneSuggestions(IEnumerable<SearchSuggestionItem> items)
        => items.ToList();
}
