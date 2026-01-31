using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Views;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IThemeService _themeService;
    private readonly AppModel _appModel;

    // UI element references for cleanup
    private Microsoft.UI.Xaml.Controls.SplitButton? _playlistsSplitButton;
    private Microsoft.UI.Xaml.Controls.MenuFlyoutItem? _newPlaylistMenuItem;
    private Microsoft.UI.Xaml.Controls.MenuFlyoutItem? _newFolderMenuItem;

    // Static collection accessible from NavigationHelpers
    public static ObservableCollection<TabBarItem> TabInstances { get; } = [];

    /// <summary>
    /// Select a tab by index - updates both SelectedTabIndex and SelectedTabItem
    /// </summary>
    public static void SelectTab(int index)
    {
        if (index >= 0 && index < TabInstances.Count)
        {
            var viewModel = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<ShellViewModel>();
            viewModel.SelectedTabIndex = index;
            viewModel.SelectedTabItem = TabInstances[index];
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

    public ShellViewModel(INavigationService navigationService, ILibraryDataService libraryDataService, IThemeService themeService, AppModel appModel)
    {
        _navigationService = navigationService;
        _libraryDataService = libraryDataService;
        _themeService = themeService;
        _appModel = appModel;

        // Initialize from AppModel (one-time read)
        _sidebarWidth = appModel.SidebarWidth;
        _selectedTabIndex = appModel.TabStripSelectedIndex;

        // Subscribe to playlist changes for reactive updates
        _libraryDataService.PlaylistsChanged += OnPlaylistsChanged;

        InitializeSidebarItems();
        _ = LoadLibraryDataAsync();
    }

    private async void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        await RefreshPlaylistsAsync();
    }

    private async Task RefreshPlaylistsAsync()
    {
        try
        {
            var playlists = await _libraryDataService.GetUserPlaylistsAsync();

            var playlistsSection = SidebarItems.FirstOrDefault(x => x.Text == "Playlists");
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
                        BadgeCount = playlist.TrackCount
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to refresh playlists: {ex.Message}");
        }
    }

    private void InitializeSidebarItems()
    {
        SidebarItems =
        [
            // Pinned section (collapsible, dynamic items)
            new SidebarItemModel
            {
                Text = "Pinned",
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
                Text = "Your Library",
                IsExpanded = true,
                Children = new ObservableCollection<SidebarItemModel>
                {
                    new SidebarItemModel
                    {
                        Text = "Albums",
                        IconSource = new FontIconSource { Glyph = "\uE93C" },
                        Tag = "Albums",
                        BadgeCount = 42 // TODO: Connect to library service
                    },
                    new SidebarItemModel
                    {
                        Text = "Artists",
                        IconSource = new FontIconSource { Glyph = "\uE77B" },
                        Tag = "Artists",
                        BadgeCount = 15 // TODO: Connect to library service
                    },
                    new SidebarItemModel
                    {
                        Text = "Liked Songs",
                        IconSource = new FontIconSource { Glyph = "\uEB52" },
                        Tag = "LikedSongs",
                        BadgeCount = 156 // TODO: Connect to library service
                    }
                }
            },
            // Playlists section (collapsible)
            new SidebarItemModel
            {
                Text = "Playlists",
                IsExpanded = true,
                ShowEmptyPlaceholder = true,
                EmptyPlaceholderText = "You have no playlists yet. Create one!",
                ItemDecorator = CreatePlaylistsAddButton(),
                Children = new ObservableCollection<SidebarItemModel>
                {
                    // User playlists will be populated dynamically
                }
            }
        ];
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
            Text = "New Playlist",
            Icon = new Microsoft.UI.Xaml.Controls.FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = "\uE93F" }
        };
        _newPlaylistMenuItem.Click += NewPlaylistMenuItem_Click;

        _newFolderMenuItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "New Folder",
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

            var stats = statsTask.Result;
            var playlists = playlistsTask.Result;

            // Update "Your Library" section badges
            var librarySection = SidebarItems.FirstOrDefault(x => x.Text == "Your Library");
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
            var playlistsSection = SidebarItems.FirstOrDefault(x => x.Text == "Playlists");
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
                        BadgeCount = playlist.TrackCount
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load library data: {ex.Message}");
        }
    }

    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        if (newValue >= 0 && newValue < TabInstances.Count)
        {
            // Set animation direction: positive = slide from right, negative = slide from left
            TabSwitchDirection = newValue > oldValue ? 1 : (newValue < oldValue ? -1 : 0);
            _previousTabIndex = oldValue;

            SelectedTabItem = TabInstances[newValue];
        }

        _appModel.TabStripSelectedIndex = newValue;
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
        UpdateNavigationState();
    }

    partial void OnSidebarWidthChanged(double value)
    {
        _appModel.SidebarWidth = value;
    }

    partial void OnSelectedSidebarItemChanged(ISidebarItemModel? value)
    {
        // Navigation is handled in ShellPage.SidebarControl_ItemInvoked
        // to support modifier keys (Ctrl/middle-click for new tab)
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
    }

    public void GoBack()
    {
        if (SelectedTabItem?.ContentFrame is Frame frame && frame.CanGoBack)
        {
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

    public void Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // Navigate to search page with query
        Helpers.Navigation.NavigationHelpers.OpenSearch(query);
    }

    public void UpdateNavigationState()
    {
        if (SelectedTabItem?.ContentFrame is Frame frame)
        {
            CanGoBack = frame.CanGoBack;
            CanGoForward = frame.CanGoForward;
            IsOnHomePage = frame.Content is HomePage;
            IsOnProfilePage = frame.Content is ProfilePage;
        }
        else
        {
            CanGoBack = false;
            CanGoForward = false;
            IsOnHomePage = false;
            IsOnProfilePage = false;
        }
    }

    /// <summary>
    /// Cleans up event subscriptions to prevent memory leaks.
    /// </summary>
    public void Cleanup()
    {
        _libraryDataService.PlaylistsChanged -= OnPlaylistsChanged;

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
}
