using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Wavee.UI.WinUI.Controls.NavigationToolbar;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }
    private InputNonClientPointerSource? _nonClientSource;

    public ShellPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        InitializeComponent();

        // Set up titlebar drag region
        SetupTitleBar();

        // Open initial tab after page is fully loaded
        Loaded += ShellPage_Loaded;
        Unloaded += ShellPage_Unloaded;

        // Subscribe to theme changes
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Set initial theme icon
        UpdateThemeIcon();
    }

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Clean up event subscriptions
        TitleBarGrid.SizeChanged -= TitleBarGrid_SizeChanged;
        TitleBarGrid.Loaded -= TitleBarGrid_Loaded;
        TabControl.SizeChanged -= TabControl_SizeChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Cleanup();
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

        // Unsubscribe to avoid duplicate calls
        Loaded -= ShellPage_Loaded;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentTheme))
        {
            UpdateThemeIcon();
        }
    }

    private void UpdateThemeIcon()
    {
        // Show moon icon in light mode (clicking will switch to dark)
        // Show sun icon in dark mode (clicking will switch to light)
        ThemeIcon.Glyph = ViewModel.CurrentTheme == ElementTheme.Light ? "\uE708" : "\uE706";
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
        catch
        {
            // InputNonClientPointerSource may not be available on all systems
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
        catch
        {
            // Ignore errors during layout
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
                        ((LibraryPage)currentPage!).SelectTab("albums");
                    else
                        NavigationHelpers.OpenAlbums(openInNewTab);
                    break;
                case "Artists":
                    if (isOnLibraryPage && !openInNewTab)
                        ((LibraryPage)currentPage!).SelectTab("artists");
                    else
                        NavigationHelpers.OpenArtists(openInNewTab);
                    break;
                case "LikedSongs":
                    if (isOnLibraryPage && !openInNewTab)
                        ((LibraryPage)currentPage!).SelectTab("likedsongs");
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
}
