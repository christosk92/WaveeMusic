using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.RightPanel;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Device picker flyout partial — mirrors RightPanelView.OutputDevice.cs but renders
/// inline below the chip in the sidebar widget. Reuses <see cref="OutputDeviceRowViewModel"/>
/// from the right panel and <see cref="FluentGlyphs"/> for icons so visuals stay consistent.
/// </summary>
public sealed partial class SidebarPlayerWidget
{
    private readonly ObservableCollection<OutputDeviceRowViewModel> _devicePickerLocalRows = new();
    private readonly ObservableCollection<OutputDeviceRowViewModel> _devicePickerConnectRows = new();
    private bool _devicePickerWired;

    private void EnsureDevicePickerWired()
    {
        if (_devicePickerWired) return;
        if (_playbackStateService == null) return;
        _devicePickerWired = true;

        DeviceFlyoutLocalList.ItemsSource = _devicePickerLocalRows;
        DeviceFlyoutConnectList.ItemsSource = _devicePickerConnectRows;

        _playbackStateService.PropertyChanged += OnDevicePickerStateChanged;
        Unloaded += (_, _) =>
        {
            if (_playbackStateService != null)
                _playbackStateService.PropertyChanged -= OnDevicePickerStateChanged;
            _devicePickerWired = false;
        };
    }

    private void OnDevicePickerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlaybackStateService.ActiveDeviceName):
            case nameof(IPlaybackStateService.IsPlayingRemotely):
            case nameof(IPlaybackStateService.ActiveDeviceType):
            case nameof(IPlaybackStateService.ActiveAudioDeviceName):
            case nameof(IPlaybackStateService.AvailableAudioDevices):
            case nameof(IPlaybackStateService.AvailableConnectDevices):
            case nameof(IPlaybackStateService.IsPlaying):
            case nameof(IPlaybackStateService.IsAudioEngineAvailable):
            case nameof(IPlaybackStateService.CurrentTrackId):
                if (DevicePickerFlyout?.IsOpen == true)
                    DispatcherQueue?.TryEnqueue(RebuildDevicePickerRows);
                break;
        }
    }

    private void DevicePickerFlyout_Opening(object sender, object e)
    {
        EnsureDevicePickerWired();
        // Best-effort rescan of live system audio devices so newly-plugged
        // outputs appear without restarting playback.
        _ = RequestDeviceListRefreshAsync();
        RebuildDevicePickerRows();
    }

    private static async Task RequestDeviceListRefreshAsync()
    {
        try
        {
            var proxy = Ioc.Default.GetService<Wavee.AudioIpc.AudioPipelineProxy>();
            if (proxy != null && proxy.IsConnected)
                await proxy.RefreshAudioDevicesAsync();
        }
        catch
        {
            // Best effort — picker still shows whatever was cached.
        }
    }

    private void RebuildDevicePickerRows()
    {
        var state = _playbackStateService;
        if (state == null) return;

        _devicePickerLocalRows.Clear();
        _devicePickerConnectRows.Clear();

        var isRemote = state.IsPlayingRemotely;
        var isPlaying = state.IsPlaying;
        var activeAudioName = state.ActiveAudioDeviceName;
        var selfDeviceId = TryGetSelfDeviceId();

        if (isRemote)
        {
            // Playing on another device: collapse Local Audio to a single
            // "This device" row that transfers playback back here.
            if (!string.IsNullOrEmpty(selfDeviceId))
            {
                _devicePickerLocalRows.Add(new OutputDeviceRowViewModel
                {
                    Name = "This device",
                    Icon = FluentGlyphs.DeviceComputer,
                    SpotifyDeviceId = selfDeviceId,
                    IsActive = false,
                    IsPlayingOnRow = false,
                });
            }
        }
        else
        {
            foreach (var device in state.AvailableAudioDevices)
            {
                var isActive = !string.IsNullOrEmpty(activeAudioName) &&
                               string.Equals(activeAudioName, device.Name, StringComparison.Ordinal);
                _devicePickerLocalRows.Add(new OutputDeviceRowViewModel
                {
                    Name = device.Name,
                    Icon = DeviceTypeToFlyoutGlyph(GuessLocalFlyoutDeviceType(device.Name)),
                    LocalDeviceIndex = device.DeviceIndex,
                    IsActive = isActive,
                    IsPlayingOnRow = isActive && isPlaying,
                });
            }
        }

        foreach (var device in state.AvailableConnectDevices)
        {
            if (!string.IsNullOrEmpty(selfDeviceId) &&
                string.Equals(device.DeviceId, selfDeviceId, StringComparison.Ordinal))
                continue;
            var isActive = isRemote && device.IsActive;
            _devicePickerConnectRows.Add(new OutputDeviceRowViewModel
            {
                Name = device.Name,
                Icon = DeviceTypeToFlyoutGlyph(device.Type),
                SpotifyDeviceId = device.DeviceId,
                IsActive = isActive,
                IsPlayingOnRow = isActive && isPlaying,
            });
        }

        DeviceFlyoutLocalHeader.Visibility = _devicePickerLocalRows.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        DeviceFlyoutConnectHeader.Visibility = _devicePickerConnectRows.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        DeviceFlyoutSeparator.Visibility = (_devicePickerLocalRows.Count > 0 && _devicePickerConnectRows.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
        DeviceFlyoutEmptyText.Visibility = (_devicePickerLocalRows.Count == 0 && _devicePickerConnectRows.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? TryGetSelfDeviceId()
    {
        try
        {
            var session = Ioc.Default.GetService<Wavee.Core.Session.Session>();
            return session?.Config?.DeviceId;
        }
        catch
        {
            return null;
        }
    }

    private async void DevicePickerRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OutputDeviceRowViewModel vm }) return;
        if (vm.IsActive) return;

        var playbackService = Ioc.Default.GetService<IPlaybackService>();
        if (playbackService == null) return;

        vm.IsSwitching = true;
        try
        {
            if (vm.LocalDeviceIndex.HasValue)
            {
                await playbackService.SwitchAudioOutputAsync(vm.LocalDeviceIndex.Value, CancellationToken.None);
            }
            else if (!string.IsNullOrEmpty(vm.SpotifyDeviceId))
            {
                await playbackService.TransferPlaybackAsync(vm.SpotifyDeviceId, startPlaying: true, CancellationToken.None);
            }
        }
        catch
        {
            // Errors flow through IPlaybackService.Errors → notification toasts.
        }
        finally
        {
            vm.IsSwitching = false;
        }

        // Dismiss the flyout once a switch is initiated.
        DevicePickerFlyout?.Hide();
    }

    private static string DeviceTypeToFlyoutGlyph(DeviceType type) => type switch
    {
        DeviceType.Smartphone => FluentGlyphs.DeviceSmartphone,
        DeviceType.Speaker => FluentGlyphs.DeviceSpeaker,
        DeviceType.CastAudio => FluentGlyphs.DeviceSpeaker,
        DeviceType.TV => FluentGlyphs.DeviceTv,
        DeviceType.CastVideo => FluentGlyphs.DeviceTv,
        DeviceType.Tablet => FluentGlyphs.DeviceTablet,
        DeviceType.AudioDongle => FluentGlyphs.DeviceHeadphones,
        DeviceType.Automobile => FluentGlyphs.DeviceCar,
        DeviceType.GameConsole => FluentGlyphs.DeviceGameConsole,
        _ => FluentGlyphs.DeviceComputer,
    };

    private static DeviceType GuessLocalFlyoutDeviceType(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return DeviceType.Computer;
        var lower = deviceName.ToLowerInvariant();
        if (lower.Contains("headphone") || lower.Contains("headset") ||
            lower.Contains("airpod") || lower.Contains("buds") || lower.Contains("earbud"))
            return DeviceType.AudioDongle;
        if (lower.Contains("speaker") || lower.Contains("realtek") || lower.Contains("wasapi"))
            return DeviceType.Speaker;
        return DeviceType.Computer;
    }
}
