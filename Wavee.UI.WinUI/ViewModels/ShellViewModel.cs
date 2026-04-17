using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using AppNotificationSeverity = Wavee.UI.WinUI.Data.Models.NotificationSeverity;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Views;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IThemeService _themeService;
    private readonly INotificationService _notificationService;
    private readonly ISearchService _searchService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly AppModel _appModel;
    private readonly IShellSessionService _shellSession;
    private readonly ILogger? _logger;
    private readonly IDispatcherService? _dispatcher;
    private readonly Helpers.Debouncer _searchDebouncer = new(TimeSpan.FromMilliseconds(300));
    private readonly Dictionary<string, CachedSearchSuggestions> _querySuggestionCache = new(StringComparer.OrdinalIgnoreCase);
    private CachedSearchSuggestions? _recentSearchesCache;
    private string _activeSearchText = string.Empty;
    private bool _restoringTabSession;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tabSleepTimer;
    private DateTimeOffset _lastTabSleepMemoryReleaseUtc = DateTimeOffset.MinValue;

    private const int MaxCachedSuggestionQueries = 24;
    private static readonly TimeSpan RecentSearchesCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuerySuggestionsCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TabSleepTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TabSleepEvaluationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TabSleepMemoryReleaseThrottle = TimeSpan.FromSeconds(45);

    // UI element references for cleanup
    private Microsoft.UI.Xaml.Controls.SplitButton? _playlistsSplitButton;
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
    /// Maps notification severity to WinUI's <see cref="InfoBarSeverity"/> for XAML binding.
    /// </summary>
    public InfoBarSeverity NotificationSeverity => _notificationService.Severity switch
    {
        AppNotificationSeverity.Informational => InfoBarSeverity.Informational,
        AppNotificationSeverity.Success => InfoBarSeverity.Success,
        AppNotificationSeverity.Warning => InfoBarSeverity.Warning,
        AppNotificationSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Error
    };

    public ShellViewModel(
        ILibraryDataService libraryDataService,
        IThemeService themeService,
        INotificationService notificationService,
        ISearchService searchService,
        IPlaybackStateService playbackStateService,
        AppModel appModel,
        IShellSessionService shellSession,
        IDispatcherService? dispatcher = null,
        ILogger<ShellViewModel>? logger = null)
    {
        _libraryDataService = libraryDataService;
        _themeService = themeService;
        _notificationService = notificationService;
        _searchService = searchService;
        _playbackStateService = playbackStateService;
        _appModel = appModel;
        _shellSession = shellSession;
        _dispatcher = dispatcher;
        _logger = logger;

        // Initialize from AppModel (one-time read)
        _sidebarWidth = appModel.SidebarWidth;
        _sidebarDisplayMode = appModel.SidebarDisplayMode;
        _isSidebarPaneOpen = appModel.IsSidebarPaneOpen;
        _rightPanelWidth = appModel.RightPanelWidth;
        _isRightPanelOpen = appModel.IsRightPanelOpen;
        _rightPanelMode = appModel.RightPanelMode;
        _selectedTabIndex = appModel.TabStripSelectedIndex;

        // Listen for right panel toggle requests from PlayerBar
        WeakReferenceMessenger.Default.Register<ToggleRightPanelMessage>(this, (r, m) =>
        {
            ((ShellViewModel)r).ToggleRightPanel(m.Value);
        });

        // Subscribe to notification service changes to forward to XAML bindings
        _notificationService.PropertyChanged += OnNotificationServicePropertyChanged;

        // Subscribe to playlist changes for reactive updates
        _libraryDataService.PlaylistsChanged += OnPlaylistsChanged;

        // Subscribe to all library data changes (sync complete, Dealer deltas, etc.)
        _libraryDataService.DataChanged += OnLibraryDataChanged;

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

        InitializeSidebarItems();
        ApplyPersistedSidebarState();
        TabInstances.CollectionChanged += OnTabInstancesCollectionChanged;
        InitializeTabSleepTimer();
        _ = LoadLibraryDataAsync();
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

    private void OnLibraryDataChanged(object? sender, EventArgs e)
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            try
            {
                _logger?.LogDebug("Library data changed — refreshing sidebar");
                await LoadLibraryDataAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to refresh library data after change");
            }
        });
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            try
            {
                await RefreshPlaylistsAsync();
            }
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
            var playlists = await _libraryDataService.GetUserPlaylistsAsync();

            var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
            if (playlistsSection?.Children is ObservableCollection<SidebarItemModel> playlistChildren)
            {
                playlistChildren.Clear();
                foreach (var playlist in playlists)
                {
                    playlistChildren.Add(new SidebarItemModel
                    {
                        Text = playlist.Name,
                        IconSource = new FontIconSource { Glyph = "\uE8FD" },
                        Tag = playlist.Id,
                        BadgeCount = playlist.TrackCount,
                        DropPredicate = payload => payload.DataFormat == "WaveeTrackIds"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh playlists from service");
            throw;
        }
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
                Children = new ObservableCollection<SidebarItemModel>
                {
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarAlbums"),
                        IconSource = new FontIconSource { Glyph = "\uE93C" },
                        Tag = "Albums",
                        BadgeCount = 42 // TODO: Connect to library service
                    },
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarArtists"),
                        IconSource = new FontIconSource { Glyph = "\uE77B" },
                        Tag = "Artists",
                        BadgeCount = 15 // TODO: Connect to library service
                    },
                    new SidebarItemModel
                    {
                        Text = AppLocalization.GetString("Shell_SidebarLikedSongs"),
                        IconSource = new FontIconSource { Glyph = "\uEB52" },
                        Tag = "LikedSongs",
                        BadgeCount = 156 // TODO: Connect to library service
                    }
                }
            },
            // Playlists section (collapsible)
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarPlaylists"),
                Tag = "Playlists",
                IsExpanded = true,
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
            if (group.Tag is string tag && _shellSession.TryGetSidebarGroupExpansion(tag, out var isExpanded))
                group.IsExpanded = isExpanded;
        }

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
            SelectedSidebarItem = FindSidebarItemByTag(selectedTag);
    }

    private SidebarItemModel? FindSidebarItemByTag(string tag)
    {
        foreach (var item in SidebarItems)
        {
            if (string.Equals(item.Tag, tag, StringComparison.Ordinal))
                return item;

            if (item.Children is IEnumerable<SidebarItemModel> children)
            {
                var child = children.FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.Ordinal));
                if (child != null)
                    return child;
            }
        }

        return null;
    }

    private void OnSidebarGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SidebarItemModel group
            || e.PropertyName != nameof(SidebarItemModel.IsExpanded)
            || string.IsNullOrWhiteSpace(group.Tag))
        {
            return;
        }

        _shellSession.UpdateSidebarGroupExpansion(group.Tag!, group.IsExpanded);
    }

    private Microsoft.UI.Xaml.FrameworkElement CreatePlaylistsAddButton()
    {
        _playlistsSplitButton = new Microsoft.UI.Xaml.Controls.SplitButton
        {
            Content = new Microsoft.UI.Xaml.Controls.FontIcon
            {
                Glyph = "\uE710",
                FontSize = 11
            },
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            Padding = new Microsoft.UI.Xaml.Thickness(4, 0, 0, 0),
            MinHeight = 20,
            Height = 20,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };

        // Main click creates a new playlist
        _playlistsSplitButton.Click += PlaylistsSplitButton_Click;

        // MenuFlyout with proper context menu styling
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
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = "\uE93F" }
        };
        _newPlaylistMenuItem.Click += NewPlaylistMenuItem_Click;

        _newFolderMenuItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = AppLocalization.GetString("Shell_NewFolder"),
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = "\uE8F4" }
        };
        _newFolderMenuItem.Click += NewFolderMenuItem_Click;

        menuFlyout.Items.Add(_newPlaylistMenuItem);
        menuFlyout.Items.Add(_newFolderMenuItem);

        _playlistsSplitButton.Flyout = menuFlyout;

        return _playlistsSplitButton;
    }

    private void PlaylistsSplitButton_Click(Microsoft.UI.Xaml.Controls.SplitButton sender, Microsoft.UI.Xaml.Controls.SplitButtonClickEventArgs args)
    {
        CreateNewPlaylist();
    }

    private void NewPlaylistMenuItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Helpers.Navigation.NavigationHelpers.OpenCreatePlaylist(isFolder: false);
    }

    private void NewFolderMenuItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Helpers.Navigation.NavigationHelpers.OpenCreatePlaylist(isFolder: true);
    }

    private void CreateNewPlaylist()
    {
        // TODO: Implement playlist creation
    }

    private void CreateNewFolder()
    {
        // TODO: Implement folder creation
    }

    private async Task LoadLibraryDataAsync()
    {
        try
        {
            // Load stats and playlists in parallel
            var statsTask = _libraryDataService.GetStatsAsync();
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(statsTask, playlistsTask);

            var stats = await statsTask;
            var playlists = await playlistsTask;

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
            }

            // Update "Playlists" section
            var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
            if (playlistsSection?.Children is ObservableCollection<SidebarItemModel> playlistChildren)
            {
                playlistChildren.Clear();
                foreach (var playlist in playlists)
                {
                    playlistChildren.Add(new SidebarItemModel
                    {
                        Text = playlist.Name,
                        IconSource = new FontIconSource { Glyph = "\uE8FD" },
                        Tag = playlist.Id,
                        BadgeCount = playlist.TrackCount,
                        DropPredicate = payload => payload.DataFormat == "WaveeTrackIds"
                    });
                }

                if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
                    SelectedSidebarItem = FindSidebarItemByTag(selectedTag);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load library data");
            ShowNotification(AppLocalization.GetString("Shell_LoadLibraryFailed"));
        }
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
            oldValue.Navigated -= TabItem_Navigated;
        }

        // Subscribe to new tab
        if (newValue != null)
        {
            newValue.Navigated += TabItem_Navigated;
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
    }

    partial void OnRightPanelModeChanged(RightPanelMode value)
    {
        _appModel.RightPanelMode = value;
        if (IsRightPanelOpen)
            WeakReferenceMessenger.Default.Send(new RightPanelStateChangedMessage(true, value));
    }

    private void ToggleRightPanel(RightPanelMode mode)
    {
        if (IsRightPanelOpen && RightPanelMode == mode)
        {
            IsRightPanelOpen = false;
        }
        else
        {
            RightPanelMode = mode;
            IsRightPanelOpen = true;
        }
    }

    partial void OnSelectedSidebarItemChanged(ISidebarItemModel? value)
    {
        // Navigation is handled in ShellPage.SidebarControl_ItemInvoked
        // to support modifier keys (Ctrl/middle-click for new tab)
        _shellSession.UpdateSelectedSidebarTag((value as SidebarItemModel)?.Tag);
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

    [ObservableProperty]
    private bool _isSearchSuggestionsLoading;

    [ObservableProperty]
    private string? _searchSuggestionErrorMessage;

    private sealed record CachedSearchSuggestions(List<SearchSuggestionItem> Items, DateTimeOffset CachedAt);

    public void Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

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
                // Empty → show recent searches immediately (no debounce)
                _searchDebouncer.Cancel();
                SearchSuggestionErrorMessage = null;

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

                if (TryGetCachedQuerySuggestions(normalizedText, out var cachedSuggestions, out var queryCacheIsFresh))
                {
                    SearchSuggestions = cachedSuggestions;
                    IsSearchSuggestionsLoading = false;
                    if (queryCacheIsFresh)
                        return;
                }
                else
                {
                    SearchSuggestions = null;
                    IsSearchSuggestionsLoading = true;
                }

                // Debounce 300ms before calling API
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

    public void RetrySearchSuggestions()
    {
        OnSearchTextChanged(_activeSearchText);
    }

    public void OnSuggestionChosen(object? item)
    {
        if (item is not SearchSuggestionItem suggestion) return;

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

    public void UpdateNavigationState()
    {
        if (SelectedTabItem?.ContentFrame is Frame frame)
        {
            CanGoBack = frame.CanGoBack;
            CanGoForward = frame.CanGoForward;
            IsOnHomePage = frame.Content is HomePage;
            IsOnProfilePage = frame.Content is ProfilePage;
            IsOnSearchPage = frame.Content is SearchPage;
        }
        else
        {
            CanGoBack = false;
            CanGoForward = false;
            IsOnHomePage = false;
            IsOnProfilePage = false;
            IsOnSearchPage = false;
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
        _libraryDataService.PlaylistsChanged -= OnPlaylistsChanged;
        _libraryDataService.DataChanged -= OnLibraryDataChanged;
        _notificationService.PropertyChanged -= OnNotificationServicePropertyChanged;
        WeakReferenceMessenger.Default.Unregister<ToggleRightPanelMessage>(this);
        TabInstances.CollectionChanged -= OnTabInstancesCollectionChanged;

        foreach (var tab in TabInstances)
            DetachTabHandlers(tab);

        foreach (var group in SidebarItems)
            group.PropertyChanged -= OnSidebarGroupPropertyChanged;

        // Cleanup sidebar button handlers
        if (_playlistsSplitButton != null)
        {
            _playlistsSplitButton.Click -= PlaylistsSplitButton_Click;
            _playlistsSplitButton = null;
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
        var suggestions = await _searchService.GetSuggestionsAsync(querySnapshot, ct);
        StoreQuerySuggestionCache(querySnapshot, suggestions);

        if (string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
        {
            SearchSuggestionErrorMessage = null;
            IsSearchSuggestionsLoading = false;
            SearchSuggestions = CloneSuggestions(suggestions);
        }
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
    }

    private void ApplySearchSuggestionFailure(string querySnapshot, Exception ex)
    {
        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        _logger?.LogWarning(ex, "Failed to fetch search suggestions");
        IsSearchSuggestionsLoading = false;

        if (SearchSuggestions is { Count: > 0 } current
            && DoSuggestionsMatchQuery(current, querySnapshot))
        {
            return;
        }

        SearchSuggestions = null;
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
