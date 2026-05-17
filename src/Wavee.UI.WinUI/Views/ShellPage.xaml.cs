using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.NavigationToolbar;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Popups;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ShellPage : Page
{
    private readonly ILogger? _logger;
    private readonly IAuthState? _authState;
    private readonly ISettingsService? _settingsService;
    private readonly IConnectivityService? _connectivity;
    private readonly IPlaybackStateService? _playbackState;
    private readonly IActiveVideoSurfaceService? _videoSurface;
    private readonly MiniVideoPlayerViewModel? _miniVideoViewModel;
    private readonly ILibraryDataService? _libraryDataService;
    private IPinService? _pinService;
    private IPlaylistMutationService? _playlistMutationService;

    private const double ZoomStep = 0.1;
    private const double ZoomMin = 0.5;
    private const double ZoomMax = 2.0;

    public ShellViewModel ViewModel { get; }
    private InputNonClientPointerSource? _nonClientSource;
    private DragStateService? _dragStateService;
    private long _sidebarOpenPaneLengthCallbackToken = -1;
    private bool _suppressVideoPromptClosedHandling;
    private string? _lastDeclinedVideoPromptTrackId;

    private Services.UiHealthMonitor? _uiHealthMonitor;
    private Controls.Diagnostics.UiHealthOverlay? _uiHealthOverlay;

    public ShellPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        NavigationHelpers.Initialize(ViewModel);
        _logger = Ioc.Default.GetService<ILogger<ShellPage>>();
        _authState = Ioc.Default.GetService<IAuthState>();
        _settingsService = Ioc.Default.GetService<ISettingsService>();
        _connectivity = Ioc.Default.GetService<IConnectivityService>();
        _playbackState = Ioc.Default.GetService<IPlaybackStateService>();
        _videoSurface = Ioc.Default.GetService<IActiveVideoSurfaceService>();
        _miniVideoViewModel = Ioc.Default.GetService<MiniVideoPlayerViewModel>();
        _libraryDataService = Ioc.Default.GetService<ILibraryDataService>();
        _playlistMutationService = Ioc.Default.GetService<IPlaylistMutationService>();
        InitializeComponent();

        // Apply saved zoom level
        InitializeZoom();

        // Listen for zoom changes from Settings UI
        var settingsVm = Ioc.Default.GetService<SettingsViewModel>();
        if (settingsVm != null)
            settingsVm.ZoomChanged += OnSettingsZoomChanged;

        // Set up titlebar drag region
        SetupTitleBar();

        // Suppress search flyout when on SearchPage
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Open initial tab after page is fully loaded
        Loaded += ShellPage_Loaded;
        Unloaded += ShellPage_Unloaded;

        // Handle track drops on sidebar playlists
        SidebarControl.ItemDropped += SidebarControl_ItemDropped;

        // Right-click menus on sidebar playlist / folder rows
        SidebarControl.ItemContextInvoked += SidebarControl_ItemContextInvoked;

        // Subscribe to drag state for app-wide overlay
        _dragStateService = Ioc.Default.GetService<DragStateService>();
        if (_dragStateService != null)
            _dragStateService.DragStateChanged += OnDragStateChanged;

        // Subscribe to connectivity state for overlay
        WeakReferenceMessenger.Default.Register<Data.Messages.ConnectivityChangedMessage>(this, (r, m) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (m.Value)
                {
                    // Connected — hide overlay
                    ConnectionOverlay.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Disconnected — show overlay
                    var isReconnecting = _connectivity?.IsReconnecting ?? false;
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
        WeakReferenceMessenger.Default.Register<MainWindowFocusReturnedMessage>(this, (r, m) =>
        {
            if (r is ShellPage shell)
                shell.DispatcherQueue.TryEnqueue(shell.TryShowVideoMiniPlayerPrompt);
        });
        UpdateUserButton();
    }

    private void OnSettingsZoomChanged(object? sender, double zoom) => ApplyZoom(zoom);

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e)
    {
#if DEBUG
        _uiHealthOverlay?.Detach();
        _uiHealthMonitor?.Dispose();
#endif

        // Clean up ShellPage-specific event subscriptions only.
        // Do NOT call ViewModel.Cleanup() here — the ViewModel is a singleton
        // and outlives the page. Its cleanup happens when the app exits.
        TitleBarGrid.SizeChanged -= TitleBarGrid_SizeChanged;
        TitleBarGrid.Loaded -= TitleBarGrid_Loaded;
        TabControl.SizeChanged -= TabControl_SizeChanged;
        SidebarControl.ItemDropped -= SidebarControl_ItemDropped;
        SidebarControl.ItemContextInvoked -= SidebarControl_ItemContextInvoked;
        if (_sidebarOpenPaneLengthCallbackToken != -1)
        {
            SidebarControl.UnregisterPropertyChangedCallback(
                SidebarView.OpenPaneLengthProperty,
                _sidebarOpenPaneLengthCallbackToken);
            _sidebarOpenPaneLengthCallbackToken = -1;
        }
        if (_dragStateService != null)
            _dragStateService.DragStateChanged -= OnDragStateChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // ShellPage_Unloaded can fire AFTER App.ShutdownHostCoreAsync has
        // disposed the IServiceProvider (window closing → Host.Dispose runs
        // first, then Page.Unloaded fires on the XAML teardown). Ioc.GetService
        // then throws ObjectDisposedException('IServiceProvider'). Guard the
        // service lookup since this is a best-effort cleanup path anyway.
        try
        {
            var settingsVm = Ioc.Default.GetService<SettingsViewModel>();
            if (settingsVm != null)
                settingsVm.ZoomChanged -= OnSettingsZoomChanged;
        }
        catch (ObjectDisposedException) { /* host already disposed — nothing to unsubscribe from */ }

        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<UserProfileUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<Data.Messages.ConnectivityChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<MainWindowFocusReturnedMessage>(this);
        WeakReferenceMessenger.Default.Send(new VideoMiniPlayerPromptStateChangedMessage(false));
    }

    private void TryShowVideoMiniPlayerPrompt()
    {
        if (_settingsService?.Settings.AskToShowVideoMiniPlayerOnFocus == false)
            return;
        if (_playbackState?.CurrentTrackIsVideo != true)
            return;
        if (_videoSurface?.HasActiveSurface != true)
            return;
        if (_miniVideoViewModel is null)
            return;
        if (_miniVideoViewModel.IsOnVideoPage || _miniVideoViewModel.IsVisible)
            return;
        if (ViewModel.IsSidebarPlayerVisibleInShell && !ViewModel.SidebarPlayerCollapsed)
            return;

        var currentTrackId = _playbackState.CurrentTrackId;
        if (!string.IsNullOrEmpty(currentTrackId)
            && string.Equals(_lastDeclinedVideoPromptTrackId, currentTrackId, StringComparison.Ordinal))
        {
            return;
        }

        if (ShowVideoHereTeachingTip.IsOpen)
            return;

        _suppressVideoPromptClosedHandling = false;
        DoNotAskShowVideoHereCheckBox.IsChecked = false;
        ShowVideoHereTeachingTip.IsOpen = true;
        WeakReferenceMessenger.Default.Send(new VideoMiniPlayerPromptStateChangedMessage(true));
    }

    private void ShowVideoHereTeachingTip_ActionButtonClick(TeachingTip sender, object args)
    {
        PersistVideoPromptOptOutIfChecked();
        _miniVideoViewModel?.ShowByUserRequest();
        CloseVideoPromptProgrammatically(sender);
    }

    private void ShowVideoHereTeachingTip_CloseButtonClick(TeachingTip sender, object args)
    {
        RememberVideoPromptDeclinedForCurrentTrack();
        PersistVideoPromptOptOutIfChecked();
        CloseVideoPromptProgrammatically(sender);
    }

    private void ShowVideoHereTeachingTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        WeakReferenceMessenger.Default.Send(new VideoMiniPlayerPromptStateChangedMessage(false));

        if (_suppressVideoPromptClosedHandling)
        {
            _suppressVideoPromptClosedHandling = false;
            return;
        }

        if (args.Reason is TeachingTipCloseReason.LightDismiss or TeachingTipCloseReason.CloseButton)
        {
            RememberVideoPromptDeclinedForCurrentTrack();
            PersistVideoPromptOptOutIfChecked();
        }
    }

    private void CloseVideoPromptProgrammatically(TeachingTip tip)
    {
        if (!tip.IsOpen)
        {
            _suppressVideoPromptClosedHandling = false;
            return;
        }

        _suppressVideoPromptClosedHandling = true;
        tip.IsOpen = false;
    }

    private void RememberVideoPromptDeclinedForCurrentTrack()
    {
        var currentTrackId = _playbackState?.CurrentTrackId;
        if (!string.IsNullOrEmpty(currentTrackId))
            _lastDeclinedVideoPromptTrackId = currentTrackId;
    }

    private void PersistVideoPromptOptOutIfChecked()
    {
        if (DoNotAskShowVideoHereCheckBox.IsChecked == true)
            _settingsService?.Update(s => s.AskToShowVideoMiniPlayerOnFocus = false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsOnSearchPage))
            NavToolbar.SuppressSearchFlyout = ViewModel.IsOnSearchPage;
        else if (e.PropertyName == nameof(ShellViewModel.IsExpandedPresentation))
            SyncTheatreFrame();
    }

    /// <summary>
    /// Navigate the TheatreFrame to VideoPlayerPage when entering an expanded
    /// presentation, clear it when returning to Normal. The frame lives at
    /// RowSpan=4 / ZIndex=10 so it covers the entire shell — the user only
    /// sees the player while expanded.
    /// </summary>
    private void SyncTheatreFrame()
    {
        _logger?.LogInformation(
            "[ShellPage.SyncTheatreFrame] expanded={Expanded} fullscreen={Fullscreen} currentSourcePageType={Current} hasContent={HasContent}",
            ViewModel.IsExpandedPresentation,
            ViewModel.IsFullscreenPresentation,
            TheatreFrame.CurrentSourcePageType?.Name ?? "<none>",
            TheatreFrame.Content is not null);
        if (ViewModel.IsExpandedPresentation)
        {
            // Check `Content` not `CurrentSourcePageType`. After
            // `Content = null` (our exit path) the Frame keeps the
            // CurrentSourcePageType in its back-stack metadata, so the
            // type-check returned true and the Navigate was skipped — but
            // the visual tree was empty. Net effect: a black overlay with
            // no content. Checking the live Content reference avoids the
            // stale-metadata trap.
            if (TheatreFrame.Content is not VideoPlayerPage)
            {
                _logger?.LogInformation("[ShellPage] TheatreFrame.Navigate(VideoPlayerPage)");
                TheatreFrame.Navigate(typeof(VideoPlayerPage));
            }
        }
        else
        {
            // Clearing the BackStack avoids the VideoPlayerPage holding a
            // reference to the MediaPlayer surface while invisible.
            _logger?.LogInformation("[ShellPage] TheatreFrame.Content = null (exit expanded)");
            TheatreFrame.Content = null;
            TheatreFrame.BackStack.Clear();
        }
    }

    private AuthStatus _previousAuthStatus = AuthStatus.Unknown;

    private void UpdateUserButton()
    {
        if (_authState == null) return;

        var status = _authState.Status;
        switch (status)
        {
            case AuthStatus.Authenticated:
                NavToolbar.IsConnecting = false;
                NavToolbar.IsAuthenticated = true;
                NavToolbar.UserDisplayName = _authState.DisplayName ?? "User";
                if (ConnectionErrorBar != null) ConnectionErrorBar.IsOpen = false;
                break;
            case AuthStatus.Authenticating:
            case AuthStatus.Unknown:
                // Still resolving; don't surface signed-out CTAs yet.
                NavToolbar.IsConnecting = true;
                NavToolbar.IsAuthenticated = false;
                NavToolbar.UserDisplayName = "Connecting...";
                if (ConnectionErrorBar != null) ConnectionErrorBar.IsOpen = false;
                break;
            case AuthStatus.Error:
                NavToolbar.IsConnecting = false;
                NavToolbar.IsAuthenticated = false;
                NavToolbar.UserDisplayName = "Connection failed";
                if (ConnectionErrorBar != null)
                {
                    ConnectionErrorBar.Message = _authState.ConnectionError ?? "Could not connect to Spotify.";
                    ConnectionErrorBar.IsOpen = true;
                }
                break;
            default: // LoggedOut / SessionExpired
                NavToolbar.IsConnecting = false;
                NavToolbar.IsAuthenticated = false;
                NavToolbar.UserDisplayName = "Sign in";
                if (ConnectionErrorBar != null) ConnectionErrorBar.IsOpen = false;
                break;
        }

        // Auto-open the sign-in dialog exactly once per transition into a signed-out state.
        // If the user dismisses it, the toolbar "Sign in" button is the way back in.
        if (IsSignedOut(status) && !IsSignedOut(_previousAuthStatus))
        {
            _ = ShowSignInDialogAsync();
        }
        _previousAuthStatus = status;
    }

    private static bool IsSignedOut(AuthStatus s)
        => s == AuthStatus.LoggedOut || s == AuthStatus.SessionExpired;

    private static int _signInDialogOpen; // Interlocked flag — VM is AddTransient, no identity guard usable.

    private async Task ShowSignInDialogAsync(bool deferred = false)
    {
        if (Interlocked.CompareExchange(ref _signInDialogOpen, 1, 0) != 0) return;
        try
        {
            var xamlRoot = MainWindow.Instance?.Content?.XamlRoot;
            if (xamlRoot == null)
            {
                if (deferred)
                {
                    // Already retried once — give up. The sign-in toolbar
                    // button stays as a manual fallback path.
                    _logger?.LogWarning("Cannot show sign-in dialog: MainWindow XamlRoot unavailable after defer.");
                    return;
                }

                // Auth status can transition into LoggedOut during
                // InitializeApplicationAsync — before ShellPage's first
                // layout pass — so XamlRoot is null at this point. Hook
                // Loaded once (or post a Low-priority dispatcher tick if
                // already loaded but the root isn't primed yet) so the
                // dialog surfaces as soon as the visual tree is realised.
                Interlocked.Exchange(ref _signInDialogOpen, 0);
                if (!IsLoaded)
                {
                    void OnLoaded(object _, RoutedEventArgs __)
                    {
                        Loaded -= OnLoaded;
                        _ = ShowSignInDialogAsync(deferred: true);
                    }
                    Loaded += OnLoaded;
                    _logger?.LogDebug("Sign-in dialog deferred until ShellPage Loaded.");
                }
                else
                {
                    var dq = DispatcherQueue ?? MainWindow.Instance?.DispatcherQueue;
                    dq?.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => _ = ShowSignInDialogAsync(deferred: true));
                    _logger?.LogDebug("Sign-in dialog deferred to next dispatcher tick.");
                }
                return;
            }

            var dialog = new SpotifyConnectDialog { XamlRoot = xamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sign-in dialog failed to show.");
        }
        finally
        {
            Interlocked.Exchange(ref _signInDialogOpen, 0);
        }
    }

    private void NavToolbar_SignInRequested(NavigationToolbar sender, RoutedEventArgs e)
    {
        _ = ShowSignInDialogAsync();
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_authState != null)
            await _authState.RetryConnectionAsync();
    }

    private async void NotificationAction_Click(object sender, RoutedEventArgs e)
    {
        var notificationService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<INotificationService>();
        if (notificationService != null)
            await notificationService.InvokeActionAsync();
    }

    private void Notification_CloseRequested(object sender, RoutedEventArgs e)
    {
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<INotificationService>()
            ?.Dismiss();
    }
    private  void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
 

        // Only open home tab if no tabs exist (first launch)
        if (ShellViewModel.TabInstances.Count == 0)
        {
            if (!ViewModel.RestorePersistedTabs())
            {
                NavigationHelpers.OpenHome(openInNewTab: true);
            }

            // Directly set SelectedTabItem since SelectedTabIndex may already be 0
            if (ShellViewModel.TabInstances.Count > 0)
                ViewModel.SelectedTabItem = ShellViewModel.TabInstances[ViewModel.SelectedTabIndex];
        }

        // FPS overlay — always available, toggled with Ctrl+Shift+F
        _uiHealthMonitor = new Services.UiHealthMonitor(DispatcherQueue, Ioc.Default.GetService<ILogger<Services.UiHealthMonitor>>());

        _uiHealthOverlay = new Controls.Diagnostics.UiHealthOverlay();
        _uiHealthOverlay.Attach(_uiHealthMonitor);
        _uiHealthOverlay.Visibility = Visibility.Collapsed;

        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_uiHealthOverlay, 4);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_uiHealthOverlay, 9999);
        RootLayoutGrid.Children.Add(_uiHealthOverlay);

        // Keep expanded album art square when sidebar is resized
        UpdateExpandedArtSize();
        if (_sidebarOpenPaneLengthCallbackToken == -1)
        {
            _sidebarOpenPaneLengthCallbackToken = SidebarControl.RegisterPropertyChangedCallback(
                SidebarView.OpenPaneLengthProperty,
                (s, dp) => UpdateExpandedArtSize());
        }

        // Lift the notification toast when the app-wide "Add to playlist"
        // bar is showing — both surfaces share the Row=2 bottom slot.
        HookAddToPlaylistSessionForToast();

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

            // Caption rect for the right-hand drag area. Caption regions are
            // enforced by Windows at the non-client level, so they survive
            // any WinUI overlay (ContentDialog smoke, popups, flyouts) that
            // would otherwise block Window.SetTitleBar's compositor-side
            // drag. Without this, opening a ContentDialog locked the user
            // out of moving the window.
            if (dragBounds.Width > 0 && dragBounds.Height > 0)
            {
                var captionRect = new RectInt32(
                    (int)(dragBounds.X * scale),
                    (int)(dragBounds.Y * scale),
                    (int)(dragBounds.Width * scale),
                    (int)(dragBounds.Height * scale));

                nonClientSource.SetRegionRects(NonClientRegionKind.Caption, [captionRect]);
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

    private void NavToolbar_FriendsRequested(NavigationToolbar sender, RoutedEventArgs args)
    {
        WeakReferenceMessenger.Default.Send(new ToggleRightPanelMessage(RightPanelMode.FriendsActivity));
    }

    private void NavToolbar_SearchQuerySubmitted(NavigationToolbar sender, string queryText)
    {
        ViewModel.Search(queryText);
    }

    private async void NavToolbar_SearchTextChanged(NavigationToolbar sender, string text)
    {
        // Async-void boundary: this is a WinUI event handler, the standard
        // place for it. The VM method is Task-returning so it can be unit-
        // tested and the await flows back here.
        try { await ViewModel.OnSearchTextChangedAsync(text); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ShellPage] OnSearchTextChanged failed: {ex}"); }
    }

    private void NavToolbar_SearchSuggestionChosen(NavigationToolbar sender, object item)
    {
        ViewModel.OnSuggestionChosen(item);
    }

    private void NavToolbar_SearchRetryRequested(NavigationToolbar sender, RoutedEventArgs args)
    {
        ViewModel.RetrySearchSuggestions();
    }

    private void NavToolbar_SearchActionButtonClicked(NavigationToolbar sender, Data.Contracts.SearchSuggestionItem item)
    {
        ViewModel.OnSuggestionActionClicked(item);
    }

    private async void SidebarControl_PinButtonClicked(object? sender, Controls.Sidebar.SidebarItemModel model)
    {
        try { await ViewModel.HandleSidebarPinButtonAsync(model); }
        catch { /* errors already logged inside the handler */ }
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
                case "Podcasts":
                case "YourEpisodes":
                    if (isOnLibraryPage && !openInNewTab)
                        (currentPage as LibraryPage)?.SelectTab("podcasts");
                    else
                        NavigationHelpers.OpenPodcasts(openInNewTab);
                    break;
                case "PodcastBrowse":
                    NavigationHelpers.OpenPodcastBrowse(openInNewTab);
                    break;
                case "LocalFiles":
                    if (!AppFeatureFlags.LocalFilesEnabled) break;
                    NavigationHelpers.OpenLocalLibrary(openInNewTab);
                    break;
                case "LocalShows":
                    if (!AppFeatureFlags.LocalFilesEnabled) break;
                    NavigationHelpers.OpenLocalShows(openInNewTab);
                    break;
                case "LocalMovies":
                    if (!AppFeatureFlags.LocalFilesEnabled) break;
                    NavigationHelpers.OpenLocalMovies(openInNewTab);
                    break;
                case "LocalMusic":
                    if (!AppFeatureFlags.LocalFilesEnabled) break;
                    NavigationHelpers.OpenLocalMusic(openInNewTab);
                    break;
                case "LocalMusicVideos":
                    if (!AppFeatureFlags.LocalFilesEnabled) break;
                    NavigationHelpers.OpenLocalMusicVideos(openInNewTab);
                    break;
                default:
                    // Pinned pseudo-URIs (Spotify "canonical pointer" entries) —
                    // match more specific before the prefix chains. Mirrors
                    // ContentCard.xaml.cs:1447–1464 so a pinned Liked-Songs /
                    // Your-Episodes row behaves the same as any other surface
                    // that opens those pages.
                    if (tag == "spotify:collection:your-episodes")
                    {
                        NavigationHelpers.OpenYourEpisodes(openInNewTab);
                        break;
                    }
                    if (tag == "spotify:collection"
                        || (tag.StartsWith("spotify:user:", StringComparison.Ordinal)
                            && tag.EndsWith(":collection", StringComparison.Ordinal)))
                    {
                        NavigationHelpers.OpenLikedSongs(openInNewTab);
                        break;
                    }

                    // Handle playlist navigation (tags starting with "spotify:playlist:").
                    // Pass a ContentNavigationParameter (not just the raw URI string) so
                    // PlaylistPage.LoadParameter takes the prefill branch — without this,
                    // sidebar nav skips PrefillFrom entirely and the hero (cover + title)
                    // stays empty until PlaylistStore.Observe pushes Ready, which can
                    // be hundreds of ms behind a fast track-cache hit. Sidebar already
                    // knows both fields (model.Text is the playlist name, model.ImageUrl
                    // is the cover URL), so prefilling is free.
                    if (tag.StartsWith("spotify:playlist:"))
                    {
                        NavigationHelpers.OpenPlaylist(
                            new Data.Parameters.ContentNavigationParameter
                            {
                                Uri = tag,
                                Title = model.Text,
                                ImageUrl = model.ImageUrl
                            },
                            model.Text,
                            openInNewTab);
                    }
                    else if (tag.StartsWith("spotify:album:"))
                    {
                        NavigationHelpers.OpenAlbum(
                            new Data.Parameters.ContentNavigationParameter
                            {
                                Uri = tag,
                                Title = model.Text,
                                ImageUrl = model.ImageUrl
                            },
                            model.Text,
                            openInNewTab);
                    }
                    else if (tag.StartsWith("spotify:artist:"))
                    {
                        NavigationHelpers.OpenArtist(
                            new Data.Parameters.ContentNavigationParameter
                            {
                                Uri = tag,
                                Title = model.Text,
                                ImageUrl = model.ImageUrl
                            },
                            model.Text,
                            openInNewTab);
                    }
                    else if (tag.StartsWith("spotify:show:"))
                    {
                        NavigationHelpers.OpenShowPage(
                            new Data.Parameters.ContentNavigationParameter
                            {
                                Uri = tag,
                                Title = model.Text,
                                ImageUrl = model.ImageUrl
                            },
                            openInNewTab);
                    }
                    break;
            }
        }
    }

    private void TabControl_TabCloseRequested(object sender, TabBarItem tab)
    {
        ViewModel.CloseTabCommand.Execute(tab);
    }

    private void TabControl_TabSleepToggleRequested(object sender, TabBarItem tab)
    {
        ViewModel.ToggleTabSleep(tab);
    }

    private void TabControl_AddTabRequested(object? sender, EventArgs e)
    {
        NavigationHelpers.OpenNewTab();
    }

    private void FpsOverlayAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_uiHealthOverlay == null) return;

        if (_uiHealthOverlay.Visibility == Visibility.Collapsed)
        {
            _uiHealthMonitor?.Start();
            _uiHealthOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            _uiHealthOverlay.Visibility = Visibility.Collapsed;
            _uiHealthMonitor?.Stop();
        }
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

    private void SidebarControl_ItemContextInvoked(object? sender, ItemContextInvokedArgs e)
    {
        if (e.Item is not SidebarItemModel model) return;
        if (string.IsNullOrEmpty(model.Tag)) return;

        System.Collections.Generic.IReadOnlyList<ContextMenuItemModel>? items = null;

        if (model.IsFolder && model.Tag.StartsWith("folder:", StringComparison.Ordinal))
        {
            items = SidebarFolderContextMenuBuilder.Build(new SidebarFolderMenuContext
            {
                FolderId = model.Tag["folder:".Length..],
                FolderName = model.Text,
                IsPinned = false
            });
        }
        else if (model.Tag.StartsWith("spotify:playlist:", StringComparison.Ordinal))
        {
            // Strictly playlist URIs only. The previous fallback ("any tag
            // without a colon") was catching library-section entries
            // (Albums / Artists / LikedSongs / Podcasts) and the Playlists
            // section header (where the "+" decorator sits), opening the
            // playlist context menu for them by mistake.
            var playlistId = model.Tag["spotify:playlist:".Length..];
            var playlistUri = "spotify:playlist:" + playlistId;
            items = SidebarPlaylistContextMenuBuilder.Build(new SidebarPlaylistMenuContext
            {
                PlaylistId = playlistId,
                PlaylistName = model.Text,
                IsInLibrary = true,
                IsOwner = model.IsOwner,
                DeleteAction = model.IsOwner
                    ? () => _ = ConfirmAndDeleteSidebarPlaylistAsync(playlistUri, model.Text)
                    : null,
                ToggleLibraryAction = model.IsOwner
                    ? null  // owners can't "remove from library" — use Delete instead (builder hides this row)
                    : () => _ = _playlistMutationService?.SetPlaylistFollowedAsync(playlistUri, followed: false)!
            });
        }

        if (items is null) return;
        ContextMenuHost.Show(SidebarControl, items, e.Position);
    }

    private async System.Threading.Tasks.Task ConfirmAndDeleteSidebarPlaylistAsync(string playlistUri, string playlistName)
    {
        if (_libraryDataService is null) return;

        var dialog = new ContentDialog
        {
            Title = "Delete playlist?",
            Content = $"\"{playlistName}\" will be removed from your library. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            await _playlistMutationService.DeletePlaylistAsync(playlistUri);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete playlist {Uri} from sidebar", playlistUri);
            ViewModel.ShowNotification("Couldn't delete the playlist");
        }
    }

    private async void SidebarControl_ItemDropped(object? sender, ItemDroppedEventArgs e)
    {
        var service = Ioc.Default.GetService<IDragDropService>();
        if (service is null) return;

        var payload = await DragPackageReader.ReadAsync(e.DroppedItem, service);
        if (payload is null) return;
        if (e.DropTarget is not SidebarItemModel model) return;

        var targetId = model.Tag;
        if (string.IsNullOrEmpty(targetId)) return;

        // Pin drop zone is wired outside the (payloadKind, targetKind) routing
        // table because it doesn't move things around — it writes to ylpin
        // via PinAsync. Short-circuit here so the registry's playlist-row /
        // folder-row routes don't try to interpret it as a rootlist target.
        if (string.Equals(targetId, ShellViewModel.SidebarPinDropZoneTag, StringComparison.Ordinal))
        {
            await HandlePinDropZoneDropAsync(payload);
            return;
        }

        var targetKind = ResolveSidebarTargetKind(model);
        var dropPosition = MapDropPosition(e.dropPosition);
        var modifiers = DragModifiersCapture.Current();

        var ctx = new DropContext(payload, targetKind, targetId, dropPosition, TargetIndex: null, modifiers);
        var result = await service.DropAsync(ctx);

        if (!result.Success && result.UserMessage is not null)
        {
            _logger?.LogWarning("Sidebar drop failed: {Message}", result.UserMessage);
            ViewModel.ShowNotification(result.UserMessage);
            return;
        }
        if (result.Success)
        {
            // Localized "added X tracks" wins for the historic track-add path so we
            // don't show two competing strings; everything else uses the handler's
            // own UserMessage (set in EnqueueTracks etc.).
            if (payload.Kind == DragPayloadKind.Tracks && targetKind == DropTargetKind.PlaylistRow)
            {
                _logger?.LogInformation("Added {TrackCount} track(s) to playlist {PlaylistId}", result.ItemsAffected, targetId);
                ViewModel.ShowNotification(string.Format(
                    AppLocalization.GetString("Playlist_AddTracksSucceeded"),
                    result.ItemsAffected));
            }
            else if (result.UserMessage is not null)
            {
                ViewModel.ShowNotification(result.UserMessage);
            }
        }
    }

    private static DropTargetKind ResolveSidebarTargetKind(SidebarItemModel model)
    {
        if (model.IsFolder || (model.Tag?.StartsWith("spotify:start-group:", StringComparison.Ordinal) ?? false))
            return DropTargetKind.FolderRow;
        return DropTargetKind.PlaylistRow;
    }

    /// <summary>
    /// Routes a drop on the sidebar's pin-drop-zone placeholder to
    /// <see cref="IPinService.PinAsync"/>, which writes the URI into
    /// the user's <c>ylpin</c> collection so it appears in the Pinned
    /// section. Accepts the payload kinds that the placeholder advertises in
    /// its <see cref="SidebarItemModel.DropPredicate"/> (playlist / album /
    /// artist / show).
    /// </summary>
    private async Task HandlePinDropZoneDropAsync(IDragPayload payload)
    {
        var pinService = _pinService ??= Ioc.Default.GetService<IPinService>();
        if (pinService is null) return;

        var uri = payload switch
        {
            PlaylistDragPayload p => p.PlaylistUri,
            AlbumDragPayload    a => a.AlbumUri,
            ArtistDragPayload   ar => ar.ArtistUri,
            ShowDragPayload     s => s.ShowUri,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(uri)) return;

        if (pinService.IsPinned(uri))
        {
            ViewModel.ShowNotification("Already pinned to the sidebar.");
            return;
        }

        try
        {
            var ok = await pinService.PinAsync(uri);
            if (ok)
                ViewModel.ShowNotification("Pinned to the sidebar.");
            else
                ViewModel.ShowNotification("Couldn't pin to the sidebar. Check your connection and try again.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PinAsync failed for {Uri}", uri);
            ViewModel.ShowNotification("Couldn't pin to the sidebar. Check your connection and try again.", InfoBarSeverity.Warning);
        }
    }

    private static DropPosition MapDropPosition(SidebarItemDropPosition pos) => pos switch
    {
        SidebarItemDropPosition.Top    => DropPosition.Before,
        SidebarItemDropPosition.Bottom => DropPosition.After,
        _                              => DropPosition.Inside,
    };

    // ── App-wide zoom (Ctrl+Plus / Ctrl+Minus / Ctrl+0) ──

    private void InitializeZoom()
    {
        if (_settingsService != null)
        {
            ZoomControl.Zoom = Math.Clamp(_settingsService.Settings.ZoomLevel, ZoomMin, ZoomMax);
        }
    }

    private void ApplyZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, ZoomMin, ZoomMax);
        ZoomControl.Zoom = zoom;

        _settingsService?.Update(s => s.ZoomLevel = zoom);
    }

    protected override void OnProcessKeyboardAccelerators(ProcessKeyboardAcceleratorEventArgs args)
    {
        // Theatre / Fullscreen shortcuts — F11 toggles fullscreen, Esc exits
        // any expanded mode. Handled before the Ctrl-modifier checks so the
        // unmodified F11 / Esc path isn't shadowed by a modifier branch.
        if (args.Modifiers == VirtualKeyModifiers.None)
        {
            if (args.Key == VirtualKey.F11)
            {
                Ioc.Default.GetService<INowPlayingPresentationService>()?.ToggleFullscreen();
                args.Handled = true;
                return;
            }
            if (args.Key == VirtualKey.Escape)
            {
                var presentation = Ioc.Default.GetService<INowPlayingPresentationService>();
                if (presentation is { IsExpanded: true })
                {
                    presentation.ExitToNormal();
                    args.Handled = true;
                    return;
                }
            }
        }

        if (args.Modifiers == VirtualKeyModifiers.Control)
        {
            switch (args.Key)
            {
                // Numpad + or OemPlus (=+ key, VirtualKey 187)
                case VirtualKey.Add:
                case (VirtualKey)187:
                    ApplyZoom(Math.Round(ZoomControl.Zoom + ZoomStep, 2));
                    args.Handled = true;
                    return;

                // Numpad - or OemMinus (-_ key, VirtualKey 189)
                case VirtualKey.Subtract:
                case (VirtualKey)189:
                    ApplyZoom(Math.Round(ZoomControl.Zoom - ZoomStep, 2));
                    args.Handled = true;
                    return;

                // Numpad 0 → reset zoom
                case VirtualKey.Number0:
                    ApplyZoom(1.0);
                    args.Handled = true;
                    return;
            }
        }

        if (args.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift))
        {
            switch (args.Key)
            {
                case VirtualKey.F:
                    FpsOverlayAccelerator_Invoked(null!, null!);
                    args.Handled = true;
                    return;

                case VirtualKey.L:
                    if (ToggleLikeCurrentTrack()) args.Handled = true;
                    return;

                case VirtualKey.S:
                    if (ToggleShuffle()) args.Handled = true;
                    return;

                // Always-live (no #if DEBUG): pops the Debug page in its own
                // floating window. Single-instance — second press focuses
                // the existing window. See Wavee.UI.WinUI.Floating.DebugFloatingWindow.
                case VirtualKey.D:
                    Wavee.UI.WinUI.Floating.DebugFloatingWindow.EnsureOpen();
                    args.Handled = true;
                    return;
            }
        }

        base.OnProcessKeyboardAccelerators(args);
    }

    // ── Playback shortcuts ──────────────────────────────────────────────

    private static bool ToggleLikeCurrentTrack()
    {
        var playback = Ioc.Default.GetService<IPlaybackStateService>();
        var like = Ioc.Default.GetService<ITrackLikeService>();
        var musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
        if (playback is null || like is null) return false;
        if (PlaybackSaveTargetResolver.GetTrackUri(playback) is null
            && (!playback.CurrentTrackIsVideo || string.IsNullOrEmpty(playback.CurrentTrackId)))
        {
            return false;
        }

        _ = ToggleLikeCurrentTrackAsync(playback, like, musicVideoMetadata);
        return true;
    }

    private static async Task ToggleLikeCurrentTrackAsync(
        IPlaybackStateService playback,
        ITrackLikeService like,
        IMusicVideoMetadataService? musicVideoMetadata)
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(playback, musicVideoMetadata)
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(uri)) return;

        var isSaved = like.IsSaved(SavedItemType.Track, uri);
        like.ToggleSave(SavedItemType.Track, uri, isSaved);
    }

    private static bool ToggleShuffle()
    {
        var playback = Ioc.Default.GetService<IPlaybackStateService>();
        if (playback is null) return false;
        playback.SetShuffle(!playback.IsShuffle);
        return true;
    }
}
