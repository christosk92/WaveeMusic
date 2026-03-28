using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Wavee.UI.WinUI.Controls.NavigationToolbar;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ShellPage : Page
{
    private readonly ILogger? _logger;

    public ShellViewModel ViewModel { get; }
    private InputNonClientPointerSource? _nonClientSource;
    private DragStateService? _dragStateService;

    public ShellPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ShellPage>>();
        InitializeComponent();

        // Set up titlebar drag region
        SetupTitleBar();

        // Suppress search flyout when on SearchPage
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsOnSearchPage))
                NavToolbar.SuppressSearchFlyout = ViewModel.IsOnSearchPage;
        };

        // Open initial tab after page is fully loaded
        Loaded += ShellPage_Loaded;
        Unloaded += ShellPage_Unloaded;

        // Handle track drops on sidebar playlists
        SidebarControl.ItemDropped += SidebarControl_ItemDropped;

        // Subscribe to drag state for app-wide overlay
        _dragStateService = Ioc.Default.GetService<DragStateService>();
        if (_dragStateService != null)
            _dragStateService.DragStateChanged += OnDragStateChanged;

        // Subscribe to connectivity state for overlay
        WeakReferenceMessenger.Default.Register<Data.Messages.ConnectivityChangedMessage>(this, (r, m) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var connectivity = Ioc.Default.GetService<IConnectivityService>();
                if (m.Value)
                {
                    // Connected — hide overlay
                    ConnectionOverlay.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Disconnected — show overlay
                    var isReconnecting = connectivity?.IsReconnecting ?? false;
                    ReconnectingRing.IsActive = isReconnecting;
                    ReconnectingRing.Visibility = isReconnecting ? Visibility.Visible : Visibility.Collapsed;
                    ConnectionOverlayText.Text = isReconnecting
                        ? "Reconnecting to Spotify..."
                        : "Connection lost";
                    ConnectionOverlay.Visibility = Visibility.Visible;
                }
            });
        });

        // Subscribe to auth state for User button display name
        WeakReferenceMessenger.Default.Register<AuthStatusChangedMessage>(this, (r, m) =>
        {
            DispatcherQueue.TryEnqueue(UpdateUserButton);
        });
        WeakReferenceMessenger.Default.Register<UserProfileUpdatedMessage>(this, (r, m) =>
        {
            DispatcherQueue.TryEnqueue(UpdateUserButton);
        });
        UpdateUserButton();
    }

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Clean up ShellPage-specific event subscriptions only.
        // Do NOT call ViewModel.Cleanup() here — the ViewModel is a singleton
        // and outlives the page. Its cleanup happens when the app exits.
        TitleBarGrid.SizeChanged -= TitleBarGrid_SizeChanged;
        TitleBarGrid.Loaded -= TitleBarGrid_Loaded;
        TabControl.SizeChanged -= TabControl_SizeChanged;
        if (_dragStateService != null)
            _dragStateService.DragStateChanged -= OnDragStateChanged;
        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<UserProfileUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.ConnectivityChangedMessage>(this);
    }

    private void UpdateUserButton()
    {
        var authState = Ioc.Default.GetService<IAuthState>();
        if (authState == null) return;

        switch (authState.Status)
        {
            case AuthStatus.Authenticated:
                NavToolbar.IsConnecting = false;
                NavToolbar.UserDisplayName = authState.DisplayName ?? "User";
                if (ConnectionErrorBar != null) ConnectionErrorBar.IsOpen = false;
                break;
            case AuthStatus.Authenticating:
                NavToolbar.IsConnecting = true;
                NavToolbar.UserDisplayName = "Connecting...";
                if (ConnectionErrorBar != null) ConnectionErrorBar.IsOpen = false;
                break;
            case AuthStatus.Error:
                NavToolbar.IsConnecting = false;
                NavToolbar.UserDisplayName = "Connection failed";
                if (ConnectionErrorBar != null)
                {
                    ConnectionErrorBar.Message = authState.ConnectionError ?? "Could not connect to Spotify.";
                    ConnectionErrorBar.IsOpen = true;
                }
                break;
            default:
                NavToolbar.IsConnecting = false;
                NavToolbar.UserDisplayName = "Sign in";
                break;
        }
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        var authState = Ioc.Default.GetService<IAuthState>();
        if (authState != null)
            await authState.RetryConnectionAsync();
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Only open home tab if no tabs exist (first launch)
        if (ShellViewModel.TabInstances.Count == 0)
        {
            NavigationHelpers.OpenHome(openInNewTab: true);
            // Directly set SelectedTabItem since SelectedTabIndex may already be 0
            ViewModel.SelectedTabItem = ShellViewModel.TabInstances[0];
        }

        // Keep expanded album art square when sidebar is resized
        UpdateExpandedArtSize();
        SidebarControl.RegisterPropertyChangedCallback(
            SidebarView.OpenPaneLengthProperty, (s, dp) => UpdateExpandedArtSize());

        // Unsubscribe to avoid duplicate calls
        Loaded -= ShellPage_Loaded;
    }

    private void UpdateExpandedArtSize()
    {
        ExpandedAlbumArtContainer.Height = SidebarControl.OpenPaneLength - 8;
    }

    private void ExpandedAlbumArt_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        PlayerBarControl.ViewModel.ToggleAlbumArtExpandedCommand.Execute(null);
    }

    private void SetupTitleBar()
    {
        // Set the titlebar drag region
        if (MainWindow.Instance.ExtendsContentIntoTitleBar)
        {
            // Set the entire TitleBarGrid as the title bar
            MainWindow.Instance.SetTitleBar(TitleBarGrid);

            // Configure the input regions:
            // - The tab strip area should be passthrough (tabs receive clicks)
            // - The drag region (empty space) remains as caption (draggable)
            ConfigureNonClientPointerSource();
        }
    }

    private void ConfigureNonClientPointerSource()
    {
        try
        {
            _nonClientSource = InputNonClientPointerSource.GetForWindowId(MainWindow.Instance.AppWindow.Id);

            // Update regions when sizes change
            TitleBarGrid.SizeChanged += TitleBarGrid_SizeChanged;
            TabControl.SizeChanged += TabControl_SizeChanged;

            // Initial update after layout
            TitleBarGrid.Loaded += TitleBarGrid_Loaded;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "InputNonClientPointerSource not available on this system");
        }
    }

    private void TitleBarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_nonClientSource != null)
            UpdateTitleBarRegions(_nonClientSource);
    }

    private void TabControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_nonClientSource != null)
            UpdateTitleBarRegions(_nonClientSource);
    }

    private void TitleBarGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_nonClientSource != null)
            UpdateTitleBarRegions(_nonClientSource);
    }

    private void UpdateTitleBarRegions(InputNonClientPointerSource nonClientSource)
    {
        try
        {
            if (TabControl.ActualWidth == 0 || TabControl.ActualHeight == 0)
                return;

            var scale = XamlRoot?.RasterizationScale ?? 1.0;

            // Get the TabView's tab strip area (excluding the footer/drag region)
            // The tab strip is the area containing the actual tabs and add button
            var tabView = TabControl.TabViewElement;
            var dragRegion = TabControl.DragRegionElement;

            // Calculate the tab strip bounds (entire TabControl minus the drag region on the right)
            var tabControlTransform = TabControl.TransformToVisual(null);
            var tabControlBounds = tabControlTransform.TransformBounds(
                new Rect(0, 0, TabControl.ActualWidth, TabControl.ActualHeight));

            // Get drag region bounds
            var dragTransform = dragRegion.TransformToVisual(null);
            var dragBounds = dragTransform.TransformBounds(
                new Rect(0, 0, dragRegion.ActualWidth, dragRegion.ActualHeight));

            // The passthrough region is the TabControl area MINUS the drag region
            // This is everything to the left of the drag region (the actual tabs)
            var passthroughWidth = tabControlBounds.Width - dragBounds.Width;
            if (passthroughWidth > 0)
            {
                var passthroughRect = new RectInt32(
                    (int)(tabControlBounds.X * scale),
                    (int)(tabControlBounds.Y * scale),
                    (int)(passthroughWidth * scale),
                    (int)(tabControlBounds.Height * scale));

                nonClientSource.SetRegionRects(NonClientRegionKind.Passthrough, [passthroughRect]);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update titlebar regions");
        }
    }

    private void NavToolbar_BackRequested(NavigationToolbar sender, RoutedEventArgs args)
    {
        ViewModel.GoBack();
    }

    private void NavToolbar_ForwardRequested(NavigationToolbar sender, RoutedEventArgs args)
    {
        ViewModel.GoForward();
    }

    private void NavToolbar_HomeRequested(NavigationToolbar sender, RoutedEventArgs args)
    {
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenHome(openInNewTab);
    }

    private void NavToolbar_SearchQuerySubmitted(NavigationToolbar sender, string queryText)
    {
        ViewModel.Search(queryText);
    }

    private void NavToolbar_SearchTextChanged(NavigationToolbar sender, string text)
    {
        ViewModel.OnSearchTextChanged(text);
    }

    private void NavToolbar_SearchSuggestionChosen(NavigationToolbar sender, object item)
    {
        ViewModel.OnSuggestionChosen(item);
    }

    private async void NavToolbar_SearchPlayRequested(NavigationToolbar sender, Data.Contracts.SearchSuggestionItem item)
    {
        await ViewModel.PlaySearchResultAsync(item);
    }

    private void SidebarControl_ItemInvoked(object? sender, ItemInvokedEventArgs e)
    {
        if (sender is not SidebarItem item || item.Item is not SidebarItemModel model)
            return;

        // Check for Ctrl key or middle-click to open in new tab
        var openInNewTab = NavigationHelpers.IsCtrlPressed() ||
                           e.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased;

        if (model.Tag is string tag)
        {
            // Check if we're already on LibraryPage for library sub-items
            var currentPage = ViewModel.SelectedTabItem?.ContentFrame?.Content;
            var isOnLibraryPage = currentPage is LibraryPage;

            switch (tag)
            {
                case "Search":
                    NavigationHelpers.OpenSearch(null, openInNewTab);
                    break;
                case "Library":
                    NavigationHelpers.OpenLibrary(openInNewTab);
                    break;
                case "Albums":
                    if (isOnLibraryPage && !openInNewTab)
                        (currentPage as LibraryPage)?.SelectTab("albums");
                    else
                        NavigationHelpers.OpenAlbums(openInNewTab);
                    break;
                case "Artists":
                    if (isOnLibraryPage && !openInNewTab)
                        (currentPage as LibraryPage)?.SelectTab("artists");
                    else
                        NavigationHelpers.OpenArtists(openInNewTab);
                    break;
                case "LikedSongs":
                    if (isOnLibraryPage && !openInNewTab)
                        (currentPage as LibraryPage)?.SelectTab("likedsongs");
                    else
                        NavigationHelpers.OpenLikedSongs(openInNewTab);
                    break;
                default:
                    // Handle playlist navigation (tags starting with "spotify:playlist:")
                    if (tag.StartsWith("spotify:playlist:"))
                    {
                        NavigationHelpers.OpenPlaylist(tag, model.Text, openInNewTab);
                    }
                    break;
            }
        }
    }

    private void TabControl_TabCloseRequested(object sender, TabBarItem tab)
    {
        ViewModel.CloseTabCommand.Execute(tab);
    }

    private void TabControl_AddTabRequested(object? sender, EventArgs e)
    {
        NavigationHelpers.OpenNewTab();
    }

    private void DebugAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigationHelpers.OpenDebug(openInNewTab: true);
        args.Handled = true;
    }

    private void OnDragStateChanged(bool isDragging)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var opacity = isDragging ? 0.3 : 1.0;
            TitleBarGrid.Opacity = opacity;
            NavToolbar.Opacity = opacity;
            PlayerBarControl.Opacity = opacity;
        });
    }

    private async void SidebarControl_ItemDropped(object? sender, ItemDroppedEventArgs e)
    {
        if (!e.DroppedItem.Contains("WaveeTrackIds")) return;
        if (e.DropTarget is not SidebarItemModel model) return;

        var playlistId = model.Tag;
        if (string.IsNullOrEmpty(playlistId) || !playlistId.StartsWith("spotify:playlist:")) return;

        try
        {
            var data = await e.DroppedItem.GetDataAsync("WaveeTrackIds") as string;
            var trackIds = data?.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (trackIds is { Length: > 0 })
            {
                // TODO: Call _libraryDataService.AddTracksToPlaylistAsync(playlistId, trackIds)
                _logger?.LogInformation("Dropped {TrackCount} track(s) onto playlist {PlaylistId}", trackIds.Length, playlistId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to handle track drop onto playlist {PlaylistId}", playlistId);
            ViewModel.ShowNotification("Failed to add tracks to playlist");
        }
    }

}
