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

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Reusable combobox-style output device picker. Self-contained:
/// subscribes to <see cref="IPlaybackStateService"/>, builds the Local Audio
/// + Spotify Connect device lists, handles row clicks via
/// <see cref="IPlaybackService"/>. Drop-in anywhere in the app —
/// the trigger renders the active device name + a chevron and opens a
/// flyout with the full picker.
///
/// <para>
/// Visuals match LibrarySortViewPanel's SortOptionRowStyle: transparent rows
/// with subtle hover/press tint, accent check glyph at right when active.
/// </para>
/// </summary>
public sealed partial class OutputDevicePicker : UserControl
{
    private readonly ObservableCollection<OutputDeviceRowViewModel> _localRows = new();
    private readonly ObservableCollection<OutputDeviceRowViewModel> _connectRows = new();
    private IPlaybackStateService? _playbackStateService;
    private bool _wired;

    public OutputDevicePicker()
    {
        InitializeComponent();
        LocalDeviceList.ItemsSource = _localRows;
        ConnectDeviceList.ItemsSource = _connectRows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureWired();
        UpdateTrigger();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged -= OnStateChanged;
        _playbackStateService = null;
        _wired = false;
    }

    private void EnsureWired()
    {
        if (_wired) return;
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        if (_playbackStateService == null) return;

        _playbackStateService.PropertyChanged += OnStateChanged;
        _wired = true;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
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
                DispatcherQueue?.TryEnqueue(() =>
                {
                    UpdateTrigger();
                    RebuildRows();
                });
                break;
        }
    }

    /// <summary>
    /// Updates the trigger label + glyph based on the current active device.
    /// Remote (Spotify Connect) devices show a remote glyph + accent foreground;
    /// local audio outputs show a speaker glyph + neutral text.
    /// </summary>
    private void UpdateTrigger()
    {
        var state = _playbackStateService;
        if (state == null) return;

        if (state.IsPlayingRemotely)
        {
            TriggerIcon.Glyph = FluentGlyphs.DeviceRemote;
            TriggerIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            TriggerLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            TriggerLabel.Text = string.IsNullOrEmpty(state.ActiveDeviceName) ? "Remote device" : state.ActiveDeviceName;
        }
        else
        {
            TriggerIcon.Glyph = FluentGlyphs.DeviceLocalSpeaker;
            TriggerIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["TextFillColorSecondaryBrush"];
            TriggerLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["TextFillColorPrimaryBrush"];
            TriggerLabel.Text = string.IsNullOrEmpty(state.ActiveAudioDeviceName) ? "This device" : state.ActiveAudioDeviceName;
        }
    }

    private void PickerFlyout_Opening(object sender, object e)
    {
        EnsureWired();
        _ = RequestDeviceListRefreshAsync();
        RebuildRows();
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
            // Best-effort.
        }
    }

    private void RebuildRows()
    {
        var state = _playbackStateService;
        if (state == null) return;

        _localRows.Clear();
        _connectRows.Clear();

        var isRemote = state.IsPlayingRemotely;
        var isPlaying = state.IsPlaying;
        var activeAudioName = state.ActiveAudioDeviceName;
        var selfDeviceId = TryGetSelfDeviceId();

        if (isRemote)
        {
            if (!string.IsNullOrEmpty(selfDeviceId))
            {
                _localRows.Add(new OutputDeviceRowViewModel
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
                _localRows.Add(new OutputDeviceRowViewModel
                {
                    Name = device.Name,
                    Icon = DeviceTypeToGlyph(GuessLocalDeviceType(device.Name)),
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
            _connectRows.Add(new OutputDeviceRowViewModel
            {
                Name = device.Name,
                Icon = DeviceTypeToGlyph(device.Type),
                SpotifyDeviceId = device.DeviceId,
                IsActive = isActive,
                IsPlayingOnRow = isActive && isPlaying,
            });
        }

        LocalHeader.Visibility = _localRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectHeader.Visibility = _connectRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListSeparator.Visibility = (_localRows.Count > 0 && _connectRows.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = (_localRows.Count == 0 && _connectRows.Count == 0)
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

        // Optimistic: flip the highlight immediately so the UI doesn't lag the IPC echo.
        foreach (var row in _localRows) row.IsActive = false;
        foreach (var row in _connectRows) row.IsActive = false;
        vm.IsActive = true;

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

        PickerFlyout?.Hide();
    }

    private static string DeviceTypeToGlyph(DeviceType type) => type switch
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

    private static DeviceType GuessLocalDeviceType(string? deviceName)
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
