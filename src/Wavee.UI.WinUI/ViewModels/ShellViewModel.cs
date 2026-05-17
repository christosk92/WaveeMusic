using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Docking;
using Wavee.UI.WinUI.ViewModels.Shell;
using Wavee.UI.WinUI.Views;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml;
using Wavee.Core.Playlists;
using Wavee.UI.Services.Search;
using AppNotificationSeverity = Wavee.UI.WinUI.Data.Models.NotificationSeverity;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Root shell ViewModel — bound at the <c>MainWindow</c> / <c>ShellPage</c>
/// level. Composes three child VMs (<see cref="Omnibar"/>, <see cref="Sidebar"/>,
/// <see cref="LinkPreview"/>) and owns the cross-cutting shell concerns that
/// don't belong to any single child: tab strip, presentation mode
/// (Normal / Theatre / Fullscreen), right panel, player location, mini
/// video player, navigation state, dialogs, library change-bus dispatch,
/// app-wide notifications.
///
/// <para>This is the second of the planned god-class refactors — the same
/// "thin composer that owns children" pattern as
/// <see cref="PlaylistViewModel"/>. The child VMs are constructor-init,
/// never replaced, and disposed in turn by the parent's <see cref="Dispose"/>.</para>
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly INotificationService _notificationService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly AppModel _appModel;
    private readonly IShellSessionService _shellSession;
    private readonly ISettingsService? _settingsService;
    private readonly IPanelDockingService _docking;
    private readonly INowPlayingPresentationService _presentation;
    private readonly MiniVideoPlayerViewModel? _miniVideoVm;
    private readonly Wavee.UI.Services.Infra.IBackgroundWorkRunner _backgroundWork;
    private readonly Wavee.UI.Services.Infra.IChangeBus _changeBus;
    private readonly IDispatcherService? _dispatcher;
    private readonly ILogger? _logger;

    private IDisposable? _changeBusPlaylistsSubscription;
    private IDisposable? _changeBusLibrarySubscription;

    private bool _restoringTabSession;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tabSleepTimer;
    private DateTimeOffset _lastTabSleepMemoryReleaseUtc = DateTimeOffset.MinValue;

    private static readonly TimeSpan TabSleepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TabSleepEvaluationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TabSleepMemoryReleaseThrottle = TimeSpan.FromSeconds(45);

    // ── Child VMs (constructor-init, never replaced) ────────────────────────

    /// <summary>Omnibar search experience — text/debounce, suggestions,
    /// recent searches, link-paste handling. XAML binds <c>Vm.Omnibar.X</c>.</summary>
    public OmnibarViewModel Omnibar { get; }

    /// <summary>Sidebar surface — items tree, pin/unpin, drag auto-expand,
    /// library badges, playlist diff. XAML binds <c>Vm.Sidebar.X</c>.</summary>
    public SidebarViewModel Sidebar { get; }

    /// <summary>Spotify URL/URI paste → entity-preview metadata fetch. Owned
    /// here and injected into <see cref="Omnibar"/>; no XAML binding.</summary>
    public LinkPreviewCoordinator LinkPreview { get; }

    /// <summary>
    /// Sidebar pin drop-zone tag — kept as a constant on the shell so
    /// <c>ShellPage</c> can route drops on the placeholder without taking a
    /// hard reference into the sidebar VM internals.
    /// </summary>
    public const string SidebarPinDropZoneTag = SidebarViewModel.SidebarPinDropZoneTag;

    // ── Tab strip (process-wide static, see NavigationHelpers) ──────────────

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

    // ── Sidebar layout state (display mode + width — owned here because
    //    they're chrome state, not part of the sidebar tree itself) ─────────

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
    /// <see cref="IsSidebarPlayerVisibleInShell"/>. Injected through the ctor
    /// — the PropertyChanged subscription is wired in the constructor body.
    /// </summary>
    public IPanelDockingService Docking => _docking;

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

    // ── Floating mini-video-player visibility ───────────────────────────────
    //
    // Compound gate: Normal presentation (no Theatre / Fullscreen takeover)
    // AND not on VideoPlayerPage in the active tab AND the mini-VM's own
    // compound says it should be visible (which already includes first-frame
    // gate, suppression flags, user-dismissed flag).
    //
    // Forwarding PropertyChanged from the mini-VM keeps this single XAML
    // binding accurate without the consumer needing to track multiple sources.
    public MiniVideoPlayerViewModel? MiniVideoPlayer => _miniVideoVm;

    private void OnMiniVideoPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniVideoPlayerViewModel.IsVisible))
            OnPropertyChanged(nameof(IsMiniPlayerVisibleInShell));
    }

    public bool IsMiniPlayerVisibleInShell =>
        IsNormalPresentation
        && !IsOnVideoPage
        && MiniVideoPlayer?.IsVisible == true;

    // ── Now-playing presentation (Theatre / Fullscreen) ─────────────────────

    public INowPlayingPresentationService Presentation => _presentation;

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
        IPinService pinService,
        IPlaylistCacheService playlistCache,
        IThemeService themeService,
        INotificationService notificationService,
        ISearchService searchService,
        IPlaybackStateService playbackStateService,
        AppModel appModel,
        IShellSessionService shellSession,
        IPanelDockingService docking,
        INowPlayingPresentationService presentation,
        OmnibarSuggestionRanker omnibarRanker,
        OmnibarSuggestionCache omnibarCache,
        ISettingsService? settingsService = null,
        IDispatcherService? dispatcher = null,
        ILogger<ShellViewModel>? logger = null,
        PlaylistMosaicService? mosaicService = null,
        Wavee.Local.ILocalLibraryService? localLibrary = null,
        ISpotifyLinkPreviewService? linkPreviewService = null,
        MiniVideoPlayerViewModel? miniVideoVm = null,
        Wavee.UI.WinUI.DragDrop.DragStateService? dragStateService = null,
        Wavee.UI.Services.Infra.IBackgroundWorkRunner? backgroundWorkRunner = null,
        Wavee.UI.Services.Infra.IChangeBus? changeBus = null)
    {
        _themeService = themeService;
        _notificationService = notificationService;
        _playbackStateService = playbackStateService;
        _appModel = appModel;
        _shellSession = shellSession;
        _docking = docking;
        _presentation = presentation;
        _miniVideoVm = miniVideoVm;
        _backgroundWork = backgroundWorkRunner ?? new Wavee.UI.Services.Infra.BackgroundWorkRunner();
        _changeBus = changeBus ?? new Wavee.UI.Services.Infra.ChangeBus();
        _settingsService = settingsService;
        _dispatcher = dispatcher;
        _logger = logger;

        // ── Construct child VMs and their cross-references ──────────────────

        LinkPreview = new LinkPreviewCoordinator(
            linkPreviewService,
            _backgroundWork,
            logger);

        Omnibar = new OmnibarViewModel(
            searchService,
            playbackStateService,
            LinkPreview,
            omnibarCache,
            omnibarRanker,
            _backgroundWork,
            localLibrary,
            activeFrameContentProvider: () => SelectedTabItem?.ContentHost?.ActivePage,
            dispatcher,
            logger);

        Sidebar = new SidebarViewModel(
            libraryDataService,
            pinService,
            playlistCache,
            shellSession,
            notificationService,
            dragStateService,
            mosaicService,
            dispatcher,
            logger);

        // Wire PropertyChanged subscriptions for the injected singletons.
        // (Previously these were wired in the lazy-resolve getters that the
        // ctor-DI move replaced.)
        _docking.PropertyChanged += OnDockingPropertyChanged;
        _presentation.PropertyChanged += OnPresentationPropertyChanged;
        if (_miniVideoVm is not null)
            _miniVideoVm.PropertyChanged += OnMiniVideoPlayerPropertyChanged;

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

        // Library / playlist change notifications now flow through IChangeBus.
        // Filter by scope; the sidebar VM owns the actual rebuild work.
        _changeBusPlaylistsSubscription = _changeBus.Changes
            .Where(static s => s == Wavee.UI.Services.Infra.ChangeScope.Playlists)
            .Subscribe(_ => Sidebar.OnPlaylistsChanged());
        _changeBusLibrarySubscription = _changeBus.Changes
            .Where(static s => s == Wavee.UI.Services.Infra.ChangeScope.Library)
            .Subscribe(_ => Sidebar.OnLibraryDataChanged());

        // Capture UI thread dispatcher for background → UI marshalling
        // Dispatcher captured via DI
        WeakReferenceMessenger.Default.Register<Data.Messages.LibrarySyncStartedMessage>(this, (_, _) =>
        {
            _dispatcher?.TryEnqueue(() =>
            {
                _logger?.LogDebug("Sidebar: sync started — clearing badges");
                Sidebar.ClearLibraryBadges();
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
            _dispatcher?.TryEnqueue(() => _backgroundWork.Run(_ => Sidebar.LoadLibraryDataAsync(), "ShellViewModel.LoadLibraryData"));
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
                    Sidebar.ClearLibrarySidebar();
                });
            }
        });

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

    // ── Sidebar pass-throughs (so call sites don't have to chase the child
    //    VM when their intent is shell-level) ────────────────────────────────

    /// <summary>Forwarder to <see cref="SidebarViewModel.SyncSidebarSelectionToPlaylist"/>.</summary>
    public void SyncSidebarSelectionToPlaylist(object? uriOrId)
        => Sidebar.SyncSidebarSelectionToPlaylist(uriOrId);

    /// <summary>Forwarder to <see cref="SidebarViewModel.SyncSidebarSelectionToTag"/>.</summary>
    public void SyncSidebarSelectionToTag(string tag)
        => Sidebar.SyncSidebarSelectionToTag(tag);

    /// <summary>Forwarder to <see cref="SidebarViewModel.HandleSidebarPinButtonAsync"/>.</summary>
    public Task HandleSidebarPinButtonAsync(SidebarItemModel model)
        => Sidebar.HandleSidebarPinButtonAsync(model);

    // ── Tab strip lifecycle ─────────────────────────────────────────────────

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

    private void TabItem_Navigated(object? sender, Wavee.UI.WinUI.Controls.PageHost.PageHostNavigatedEventArgs e)
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
    private Wavee.UI.Contracts.ITrackItem? _selectedTrackForDetails;

    /// <summary>
    /// Open the right panel with the temporary "Track details" tab showing metadata for
    /// <paramref name="track"/>. No-op when <paramref name="track"/> is null.
    /// </summary>
    public void ShowTrackDetails(Wavee.UI.Contracts.ITrackItem? track)
    {
        if (track is null) return;
        SelectedTrackForDetails = track;
        RightPanelMode = RightPanelMode.TrackDetails;
        IsRightPanelOpen = true;
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
        if (SelectedTabItem?.ContentHost is { CanGoBack: true } host)
        {
            host.GoBack();
            UpdateNavigationState();
        }
    }

    public void GoForward()
    {
        if (SelectedTabItem?.ContentHost is { CanGoForward: true } host)
        {
            host.GoForward();
            UpdateNavigationState();
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
        if (SelectedTabItem?.ContentHost is { } host)
        {
            CanGoBack = host.CanGoBack;
            CanGoForward = host.CanGoForward;
            IsOnHomePage = host.ActivePage is HomePage;
            IsOnProfilePage = host.ActivePage is ProfilePage;
            IsOnSearchPage = host.ActivePage is SearchPage;
            onVideo = host.ActivePage is Wavee.UI.WinUI.Views.VideoPlayerPage;

            if (host.ActivePage?.GetType() is { } contentType
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
        _miniVideoVm?.SetOnVideoPage(onVideo);
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

        Omnibar.Dispose();
        Sidebar.Dispose();
        // LinkPreview's Dispose() is invoked transitively via Omnibar.Dispose.

        _changeBusPlaylistsSubscription?.Dispose();
        _changeBusLibrarySubscription?.Dispose();

        _notificationService.PropertyChanged -= OnNotificationServicePropertyChanged;
        // Match the 5 Register<T> calls in the constructor — the 4 beyond
        // ToggleRightPanelMessage were omitted, leaving each handler closure
        // pinning the ShellViewModel (captured `this`) against GC. Although
        // ShellViewModel is effectively a singleton per session so the leak
        // is bounded, the closure chain also roots ILibraryDataService +
        // DispatcherQueue references, which matters if the VM is ever
        // reconstructed (e.g. on sign-out / sign-in cycles).
        WeakReferenceMessenger.Default.Unregister<ToggleRightPanelMessage>(this);
        WeakReferenceMessenger.Default.Unregister<DockedPlayerWithFloatingPlayerVisibilityChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.LibrarySyncStartedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.LibrarySyncFailedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.LibrarySyncCompletedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        TabInstances.CollectionChanged -= OnTabInstancesCollectionChanged;

        foreach (var tab in TabInstances)
            DetachTabHandlers(tab);

        _docking.PropertyChanged -= OnDockingPropertyChanged;
        _presentation.PropertyChanged -= OnPresentationPropertyChanged;
        if (_miniVideoVm is not null)
            _miniVideoVm.PropertyChanged -= OnMiniVideoPlayerPropertyChanged;
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
}
