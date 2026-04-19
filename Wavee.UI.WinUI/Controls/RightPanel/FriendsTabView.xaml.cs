using System;
using System.ComponentModel;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class FriendsTabView : UserControl
{
    private readonly IFriendsFeedService? _service;
    private readonly INotifyPropertyChanged? _serviceNpc;

    public FriendsTabView()
    {
        InitializeComponent();

        _service = Ioc.Default.GetService<IFriendsFeedService>();
        if (_service != null)
        {
            FriendsRepeater.ItemsSource = _service.Items;
            _serviceNpc = _service as INotifyPropertyChanged;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        if (_serviceNpc != null) _serviceNpc.PropertyChanged += OnServicePropertyChanged;
        _service.FriendUpserted += OnFriendUpserted;
        UpdateStateVisuals();
        _service.SetActive(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        if (_serviceNpc != null) _serviceNpc.PropertyChanged -= OnServicePropertyChanged;
        _service.FriendUpserted -= OnFriendUpserted;
        _service.SetActive(false);
    }

    private void OnFriendUpserted(string userUri)
    {
        if (_service == null || string.IsNullOrEmpty(userUri)) return;

        // Find the index of the row in Items.
        int index = -1;
        for (int i = 0; i < _service.Items.Count; i++)
        {
            if (string.Equals(_service.Items[i].UserUri, userUri, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return;

        // Resolve the realized element. If layout hasn't produced it yet,
        // defer one dispatcher tick.
        var element = FriendsRepeater.TryGetElement(index);
        if (element == null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var deferred = FriendsRepeater.TryGetElement(index);
                if (deferred != null) AnimateHighlight(deferred);
            });
            return;
        }

        AnimateHighlight(element);
    }

    private static void AnimateHighlight(UIElement element)
    {
        // Scale bump on the whole row.
        AnimationBuilder.Create()
            .Scale(
                to: new Vector3(1.025f, 1.025f, 1f),
                duration: TimeSpan.FromMilliseconds(140),
                easingType: EasingType.Cubic,
                easingMode: EasingMode.EaseOut)
            .Scale(
                to: new Vector3(1f, 1f, 1f),
                delay: TimeSpan.FromMilliseconds(140),
                duration: TimeSpan.FromMilliseconds(340),
                easingType: EasingType.Cubic,
                easingMode: EasingMode.EaseInOut)
            .Start(element);

        // Accent glow: fade the overlay Border in then out.
        var glow = FindGlowOverlay(element);
        if (glow != null)
        {
            AnimationBuilder.Create()
                .Opacity(
                    to: 0.35,
                    duration: TimeSpan.FromMilliseconds(160),
                    easingType: EasingType.Cubic,
                    easingMode: EasingMode.EaseOut)
                .Opacity(
                    to: 0.0,
                    delay: TimeSpan.FromMilliseconds(220),
                    duration: TimeSpan.FromMilliseconds(520),
                    easingType: EasingType.Cubic,
                    easingMode: EasingMode.EaseInOut)
                .Start(glow);
        }
    }

    private static Border? FindGlowOverlay(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b && b.Tag is string t && t == "HighlightGlow")
                return b;

            var nested = FindGlowOverlay(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IFriendsFeedService.State)
            or nameof(IFriendsFeedService.ErrorMessage))
        {
            UpdateStateVisuals();
        }
    }

    private void UpdateStateVisuals()
    {
        if (_service == null) return;

        LoadingPanel.Visibility = _service.State == FriendsFeedState.Loading
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _service.State == FriendsFeedState.Empty
            ? Visibility.Visible : Visibility.Collapsed;
        OfflinePanel.Visibility = _service.State == FriendsFeedState.Offline
            ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = _service.State == FriendsFeedState.Error
            ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = _service.State == FriendsFeedState.Populated
            ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(_service.ErrorMessage))
            ErrorText.Text = _service.ErrorMessage;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _ = _service?.RefreshAsync();
    }

    private void JamDismiss_Click(object sender, RoutedEventArgs e)
    {
        JamCard.Visibility = Visibility.Collapsed;
    }

    private async void StartJam_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Jam is not available yet",
            Content = "Jam sessions aren't supported in Wavee yet. Stay tuned!",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

}
