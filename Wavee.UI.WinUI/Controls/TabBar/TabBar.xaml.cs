using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Controls.TabBar;

public sealed partial class TabBar : UserControl
{
    public TabBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets the drag region element (the empty space to the right of tabs).
    /// This can be used to configure the title bar drag area.
    /// </summary>
    public FrameworkElement DragRegionElement => DragRegion;

    /// <summary>
    /// Gets the TabView control for accessing tab strip bounds.
    /// </summary>
    public TabView TabViewElement => TabViewControl;

    // TabInstances - the ONLY collection, bound directly to TabView
    public static readonly DependencyProperty TabInstancesProperty =
        DependencyProperty.Register(nameof(TabInstances), typeof(ObservableCollection<TabBarItem>), typeof(TabBar),
            new PropertyMetadata(null));

    public ObservableCollection<TabBarItem>? TabInstances
    {
        get => (ObservableCollection<TabBarItem>?)GetValue(TabInstancesProperty);
        set => SetValue(TabInstancesProperty, value);
    }

    // SelectedIndex - two-way bound to ViewModel
    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TabBar),
            new PropertyMetadata(-1, OnSelectedIndexChanged));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TabBar tabBar && e.NewValue is int index)
        {
            // Sync to TabView if different
            if (tabBar.TabViewControl.SelectedIndex != index)
            {
                tabBar.TabViewControl.SelectedIndex = index;
            }
        }
    }

    // Events - just forward to ViewModel, don't handle internally
    public event EventHandler<TabBarItem>? TabCloseRequested;
    public event EventHandler? AddTabRequested;

    private void TabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Update our SelectedIndex from TabView's selection
        SelectedIndex = TabViewControl.SelectedIndex;
    }

    private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // Just raise event - ViewModel handles the actual close
        if (args.Item is TabBarItem tabItem)
        {
            TabCloseRequested?.Invoke(this, tabItem);
        }
    }

    private void TabView_AddTabButtonClick(TabView sender, object args)
    {
        AddTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is TabBarItem tab)
        {
            TabCloseRequested?.Invoke(this, tab);
        }
    }

    private void CloseTabsToLeft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is TabBarItem tab && TabInstances != null)
        {
            var index = TabInstances.IndexOf(tab);
            for (int i = index - 1; i >= 0; i--)
            {
                TabCloseRequested?.Invoke(this, TabInstances[i]);
            }
        }
    }

    private void CloseTabsToRight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is TabBarItem tab && TabInstances != null)
        {
            var index = TabInstances.IndexOf(tab);
            for (int i = TabInstances.Count - 1; i > index; i--)
            {
                TabCloseRequested?.Invoke(this, TabInstances[i]);
            }
        }
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is TabBarItem tab && TabInstances != null)
        {
            var tabsToClose = TabInstances.Where(t => t != tab).ToList();
            foreach (var t in tabsToClose)
            {
                TabCloseRequested?.Invoke(this, t);
            }
        }
    }

    private void TabViewItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // Prevent the system window context menu from appearing
        args.Handled = true;

        // Show our context flyout
        if (sender is TabViewItem tabViewItem && tabViewItem.ContextFlyout is MenuFlyout flyout)
        {
            if (args.TryGetPosition(tabViewItem, out var point))
            {
                flyout.ShowAt(tabViewItem, point);
            }
            else
            {
                // Keyboard or touch without position - show at center
                flyout.ShowAt(tabViewItem);
            }
        }
    }

    private void ContextMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            // Find the tab this menu belongs to
            TabBarItem? tab = null;
            foreach (var item in flyout.Items)
            {
                if (item is MenuFlyoutItem menuItem && menuItem.Tag is TabBarItem t)
                {
                    tab = t;
                    break;
                }
            }

            if (tab != null)
            {
                // Update menu text and icons based on current state
                foreach (var item in flyout.Items)
                {
                    if (item is MenuFlyoutItem menuItem)
                    {
                        if (menuItem.Text == "Pin" || menuItem.Text == "Unpin")
                        {
                            menuItem.Text = tab.IsPinned ? "Unpin" : "Pin";
                            if (menuItem.Icon is FontIcon pinIcon)
                            {
                                pinIcon.Glyph = tab.IsPinned ? "\uE77A" : "\uE718"; // Unpin : Pin
                            }
                        }
                        else if (menuItem.Text == "Compact" || menuItem.Text == "Expand")
                        {
                            menuItem.Text = tab.IsCompact ? "Expand" : "Compact";
                            if (menuItem.Icon is FontIcon compactIcon)
                            {
                                compactIcon.Glyph = tab.IsCompact ? "\uE923" : "\uE921"; // Restore : Minimize
                            }
                        }
                    }
                }
            }
        }
    }

    private void PinTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is TabBarItem tab)
        {
            tab.IsPinned = !tab.IsPinned;
            SortTabs();
        }
    }

    private void CompactTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is TabBarItem tab)
        {
            tab.IsCompact = !tab.IsCompact;
        }
    }

    private void SortTabs()
    {
        if (TabInstances == null) return;

        // Remember current selection
        var selectedTab = TabViewControl.SelectedItem as TabBarItem;

        // Sort: pinned tabs first, then unpinned
        var pinned = TabInstances.Where(t => t.IsPinned).ToList();
        var unpinned = TabInstances.Where(t => !t.IsPinned).ToList();

        TabInstances.Clear();
        foreach (var t in pinned) TabInstances.Add(t);
        foreach (var t in unpinned) TabInstances.Add(t);

        // Restore selection
        if (selectedTab != null)
        {
            var newIndex = TabInstances.IndexOf(selectedTab);
            if (newIndex >= 0)
            {
                SelectedIndex = newIndex;
            }
        }
    }
}
