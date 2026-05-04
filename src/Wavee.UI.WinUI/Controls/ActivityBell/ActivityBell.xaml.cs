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
    private readonly INotifyPropertyChanged? _serviceNotifyPropertyChanged;
    private readonly INotifyCollectionChanged? _itemsNotifyCollectionChanged;
    private bool _subscriptionsAttached;

    public ActivityBell()
    {
        InitializeComponent();

        _service = Ioc.Default.GetService<IActivityService>();
        if (_service == null) return;

        ActivityList.ItemsSource = _service.Items;
        _serviceNotifyPropertyChanged = _service as INotifyPropertyChanged;
        _itemsNotifyCollectionChanged = _service.Items as INotifyCollectionChanged;

        Loaded += ActivityBell_Loaded;
        Unloaded += ActivityBell_Unloaded;
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

    private void ActivityBell_Loaded(object sender, RoutedEventArgs e)
    {
        AttachSubscriptions();
        UpdateBadge();
        UpdateEmptyState();
    }

    private void ActivityBell_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachSubscriptions();
    }

    private void AttachSubscriptions()
    {
        if (_subscriptionsAttached)
            return;

        if (_serviceNotifyPropertyChanged != null)
            _serviceNotifyPropertyChanged.PropertyChanged += OnServicePropertyChanged;

        if (_itemsNotifyCollectionChanged != null)
            _itemsNotifyCollectionChanged.CollectionChanged += OnItemsCollectionChanged;

        _subscriptionsAttached = true;
    }

    private void DetachSubscriptions()
    {
        if (!_subscriptionsAttached)
            return;

        if (_serviceNotifyPropertyChanged != null)
            _serviceNotifyPropertyChanged.PropertyChanged -= OnServicePropertyChanged;

        if (_itemsNotifyCollectionChanged != null)
            _itemsNotifyCollectionChanged.CollectionChanged -= OnItemsCollectionChanged;

        _subscriptionsAttached = false;
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IActivityService.UnreadCount))
            UpdateBadge();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
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
