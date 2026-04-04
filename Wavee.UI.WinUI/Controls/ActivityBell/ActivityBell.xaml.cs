using System;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Controls.ActivityBell;

public sealed partial class ActivityBell : UserControl
{
    private readonly IActivityService? _service;

    public ActivityBell()
    {
        InitializeComponent();

        _service = Ioc.Default.GetService<IActivityService>();
        if (_service == null) return;

        ActivityList.ItemsSource = _service.Items;

        // Track unread count for badge
        if (_service is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IActivityService.UnreadCount))
                    UpdateBadge();
            };
        }

        // Update empty state when items change
        ((INotifyCollectionChanged)_service.Items).CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateBadge();
        UpdateEmptyState();
    }

    private void UpdateBadge()
    {
        if (_service == null) return;
        var count = _service.UnreadCount;
        UnreadBadge.Value = count;
        UnreadBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        if (_service == null) return;
        var hasItems = _service.Items.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ActivityList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        _service?.MarkAllRead();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _service?.ClearAll();
    }

    private async void ActivityAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ActivityAction action)
            return;

        try
        {
            btn.IsEnabled = false;
            await action.Callback();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activity action failed: {ex.Message}");
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }
}
