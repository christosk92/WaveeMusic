using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView
{
    // ── State for the Output Device card ──

    private bool _deviceCardExpanded;
    private bool _deviceCardListenerWired;
    private IPlaybackStateService? _deviceCardPlaybackState;
    private readonly ObservableCollection<OutputDeviceRowViewModel> _localDeviceRows = new();
    private readonly ObservableCollection<OutputDeviceRowViewModel> _connectDeviceRows = new();

    // Fallback glyphs (Segoe Fluent Icons)
    private const string GlyphComputer = "\uE977";
    private const string GlyphPhone = "\uE8EA";
    private const string GlyphSpeaker = "\uE995";
    private const string GlyphTv = "\uE7F4";
    private const string GlyphTablet = "\uE70A";
    private const string GlyphHeadphones = "\uE7F6";
    private const string GlyphCar = "\uE804";
    private const string GlyphGameConsole = "\uE7FC";

    /// <summary>
    /// Wire the device card to <see cref="IPlaybackStateService"/>. Safe to call multiple times.
    /// Must be invoked after <c>DetailsContent</c> has been materialized.
    /// </summary>
    private void InitializeOutputDeviceCard()
    {
        if (DetailsContent == null || DetailsOutputDeviceCard == null)
            return;

        if (_deviceCardListenerWired)
            return;

        var state = _lyricsVm?.PlaybackState;
        if (state == null)
            return;

        _deviceCardPlaybackState = state;
        state.PropertyChanged += OnOutputDevicePlaybackStateChanged;

        // ItemsRepeater.ItemTemplate is declared in XAML as a static resource
        // (OutputDeviceRowTemplate) — we just need to wire the ItemsSource here.
        DetailsLocalDeviceList.ItemsSource = _localDeviceRows;
        DetailsConnectDeviceList.ItemsSource = _connectDeviceRows;

        _deviceCardListenerWired = true;

        UpdateOutputDeviceCard();
    }

    private void TeardownOutputDeviceCard()
    {
        if (_deviceCardPlaybackState != null)
        {
            _deviceCardPlaybackState.PropertyChanged -= OnOutputDevicePlaybackStateChanged;
            _deviceCardPlaybackState = null;
        }
        _deviceCardListenerWired = false;
    }

    private void OnOutputDevicePlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
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
                DispatcherQueue.TryEnqueue(UpdateOutputDeviceCard);
                break;
        }
    }

    private void UpdateOutputDeviceCard()
    {
        var state = _deviceCardPlaybackState;
        if (state == null || DetailsOutputDeviceCard == null)
            return;

        // Hide the card entirely until we have a track loaded
        var hasTrack = !string.IsNullOrEmpty(state.CurrentTrackId);
        DetailsOutputDeviceCard.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        if (!hasTrack)
            return;

        // Offline state overrides everything else
        if (!state.IsAudioEngineAvailable)
        {
            DetailsDeviceCardHeader.Visibility = Visibility.Collapsed;
            DetailsDeviceExpandedPanel.Visibility = Visibility.Collapsed;
            DetailsDeviceOfflinePanel.Visibility = Visibility.Visible;
            DetailsOutputDeviceCard.Opacity = 0.7;
            return;
        }

        DetailsDeviceCardHeader.Visibility = Visibility.Visible;
        DetailsDeviceOfflinePanel.Visibility = Visibility.Collapsed;
        DetailsOutputDeviceCard.Opacity = 1.0;

        var isRemote = state.IsPlayingRemotely;
        DetailsDeviceLabel.Text = isRemote ? "Connected to" : "Playing on";

        var displayName = isRemote
            ? state.ActiveDeviceName ?? "Remote device"
            : state.ActiveAudioDeviceName ?? "This device";
        DetailsDeviceName.Text = displayName;

        var effectiveType = isRemote ? state.ActiveDeviceType : GuessLocalDeviceType(state.ActiveAudioDeviceName);
        DetailsDeviceIcon.Glyph = DeviceTypeToGlyph(effectiveType);

        if (_deviceCardExpanded)
            RebuildDeviceLists();
    }

    private void RebuildDeviceLists()
    {
        var state = _deviceCardPlaybackState;
        if (state == null) return;

        _localDeviceRows.Clear();
        _connectDeviceRows.Clear();

        var isRemote = state.IsPlayingRemotely;
        var isPlaying = state.IsPlaying;
        var activeAudioName = state.ActiveAudioDeviceName;
        var selfDeviceId = TryGetSelfDeviceId();

        if (isRemote)
        {
            // Playing on another Spotify device: LOCAL AUDIO section collapses to a single
            // "This device" row. Tapping it transfers playback back to us via Spotify Connect.
            // Individual WASAPI endpoint switching isn't meaningful until we're playing locally.
            if (!string.IsNullOrEmpty(selfDeviceId))
            {
                _localDeviceRows.Add(new OutputDeviceRowViewModel
                {
                    Name = "This device",
                    Icon = GlyphComputer,
                    SpotifyDeviceId = selfDeviceId,
                    IsActive = false,
                    IsPlayingOnRow = false,
                });
            }
        }
        else
        {
            // Playing locally: LOCAL AUDIO section shows every PortAudio output endpoint
            // so the user can switch between headphones/speakers/etc.
            foreach (var device in state.AvailableAudioDevices)
            {
                var isActive = !string.IsNullOrEmpty(activeAudioName) &&
                               string.Equals(activeAudioName, device.Name, StringComparison.Ordinal);

                _localDeviceRows.Add(new OutputDeviceRowViewModel
                {
                    Name = device.Name,
                    Icon = DeviceTypeToGlyph(GuessLocalDeviceType(device.Name)),
                    LocalDeviceIndex = device.DeviceIndex,
                    IsActive = isActive,
                    IsPlayingOnRow = isActive && isPlaying,
                });
            }
        }

        // Spotify Connect devices (excluding our own local device — it's represented either
        // by the WASAPI list above when playing locally, or by the "This device" row above
        // when playing remotely).
        foreach (var device in state.AvailableConnectDevices)
        {
            if (!string.IsNullOrEmpty(selfDeviceId) &&
                string.Equals(device.DeviceId, selfDeviceId, StringComparison.Ordinal))
                continue;

            var isActive = isRemote && device.IsActive;
            _connectDeviceRows.Add(new OutputDeviceRowViewModel
            {
                Name = device.Name,
                Icon = DeviceTypeToGlyph(device.Type),
                SpotifyDeviceId = device.DeviceId,
                IsActive = isActive,
                IsPlayingOnRow = isActive && isPlaying,
            });
        }

        DetailsLocalDeviceHeader.Visibility = _localDeviceRows.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsConnectDeviceHeader.Visibility = _connectDeviceRows.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsDeviceSectionSeparator.Visibility = (_localDeviceRows.Count > 0 && _connectDeviceRows.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsNoOtherDevicesText.Visibility = (_localDeviceRows.Count == 0 && _connectDeviceRows.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? TryGetSelfDeviceId()
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

    // ── Event handlers (wired in XAML) ──

    private void DetailsDeviceCardHeader_Click(object sender, RoutedEventArgs e)
    {
        _deviceCardExpanded = !_deviceCardExpanded;
        AnimateChevron(_deviceCardExpanded ? 180 : 0);
        DetailsDeviceExpandedPanel.Visibility = _deviceCardExpanded
            ? Visibility.Visible : Visibility.Collapsed;
        if (_deviceCardExpanded)
        {
            // Ask AudioHost to rescan the live system device list so newly-plugged
            // devices (Bluetooth headphones etc.) appear without requiring playback
            // to stop. The fresh list arrives asynchronously via the next state
            // snapshot; RebuildDeviceLists will re-run when it does.
            _ = RequestAudioDeviceRefreshAsync();
            RebuildDeviceLists();
        }
    }

    private async Task RequestAudioDeviceRefreshAsync()
    {
        try
        {
            var proxy = Ioc.Default.GetService<Wavee.AudioIpc.AudioPipelineProxy>();
            if (proxy != null && proxy.IsConnected)
                await proxy.RefreshAudioDevicesAsync();
        }
        catch
        {
            // Best-effort — the picker still shows whatever was cached.
        }
    }

    private async void DeviceRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OutputDeviceRowViewModel vm })
            return;
        if (vm.IsActive)
            return;

        var playbackService = Ioc.Default.GetService<IPlaybackService>();
        if (playbackService == null)
            return;

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
            // Swallow — errors flow through IPlaybackService.Errors observable which
            // surfaces toasts via the notification service elsewhere.
        }
        finally
        {
            vm.IsSwitching = false;
        }

        // Collapse after a successful switch
        _deviceCardExpanded = false;
        AnimateChevron(0);
        DetailsDeviceExpandedPanel.Visibility = Visibility.Collapsed;
    }

    private void DetailsDeviceRetry_Click(object sender, RoutedEventArgs e)
    {
        // Audio engine availability is driven by IPC connect/disconnect on the
        // PlaybackStateService side — a simple refresh is the best we can do from here.
        // The proxy will auto-reconnect on the next command.
        UpdateOutputDeviceCard();
    }

    private void AnimateChevron(double targetAngle)
    {
        var rotation = DetailsDeviceChevronRotation;
        if (rotation == null) return;

        var anim = new DoubleAnimation
        {
            To = targetAngle,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, rotation);
        Storyboard.SetTargetProperty(anim, "Angle");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private static string DeviceTypeToGlyph(DeviceType type) => type switch
    {
        DeviceType.Smartphone => GlyphPhone,
        DeviceType.Speaker => GlyphSpeaker,
        DeviceType.CastAudio => GlyphSpeaker,
        DeviceType.TV => GlyphTv,
        DeviceType.CastVideo => GlyphTv,
        DeviceType.Tablet => GlyphTablet,
        DeviceType.AudioDongle => GlyphHeadphones,
        DeviceType.Automobile => GlyphCar,
        DeviceType.GameConsole => GlyphGameConsole,
        _ => GlyphComputer,
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

/// <summary>
/// Row view-model for the Output Device card. One instance per enumerated device
/// (either a local PortAudio device or a Spotify Connect device from the cluster).
/// </summary>
public sealed partial class OutputDeviceRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Icon { get; init; }

    /// <summary>Non-null when this row represents a local PortAudio output device.</summary>
    public int? LocalDeviceIndex { get; init; }

    /// <summary>Non-null when this row represents a Spotify Connect device.</summary>
    public string? SpotifyDeviceId { get; init; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isPlayingOnRow;
    [ObservableProperty] private bool _isSwitching;

    public Brush? RowBackground => IsActive
        ? (Application.Current.Resources["AccentFillColorTertiaryBrush"] as Brush)
        : null;

    public Brush? RowForeground => IsActive
        ? (Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush)
        : (Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush);

    public Visibility EqualizerVisibility => IsActive && !IsSwitching
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SpinnerVisibility => IsSwitching
        ? Visibility.Visible
        : Visibility.Collapsed;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(RowForeground));
        OnPropertyChanged(nameof(EqualizerVisibility));
    }

    partial void OnIsSwitchingChanged(bool value)
    {
        OnPropertyChanged(nameof(EqualizerVisibility));
        OnPropertyChanged(nameof(SpinnerVisibility));
    }
}
