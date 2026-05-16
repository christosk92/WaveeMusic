using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.Playback;

public enum AudioOutputPickerDisplayMode
{
    Compact,
    Card
}

public sealed partial class AudioOutputPicker : UserControl
{
    public static readonly DependencyProperty DisplayModeProperty =
        DependencyProperty.Register(
            nameof(DisplayMode),
            typeof(AudioOutputPickerDisplayMode),
            typeof(AudioOutputPicker),
            new PropertyMetadata(AudioOutputPickerDisplayMode.Compact, OnDisplayModeChanged));

    public static readonly DependencyProperty HideWhenNoTrackProperty =
        DependencyProperty.Register(
            nameof(HideWhenNoTrack),
            typeof(bool),
            typeof(AudioOutputPicker),
            new PropertyMetadata(false, OnVisibilityOptionChanged));

    public static readonly DependencyProperty CompactMaxWidthProperty =
        DependencyProperty.Register(
            nameof(CompactMaxWidth),
            typeof(double),
            typeof(AudioOutputPicker),
            new PropertyMetadata(220d, OnCompactMaxWidthChanged));

    public static readonly DependencyProperty CompactIconOnlyProperty =
        DependencyProperty.Register(
            nameof(CompactIconOnly),
            typeof(bool),
            typeof(AudioOutputPicker),
            new PropertyMetadata(false, OnCompactChromeChanged));

    public static readonly DependencyProperty CompactFlyoutPlacementProperty =
        DependencyProperty.Register(
            nameof(CompactFlyoutPlacement),
            typeof(FlyoutPlacementMode),
            typeof(AudioOutputPicker),
            new PropertyMetadata(FlyoutPlacementMode.BottomEdgeAlignedLeft, OnCompactChromeChanged));

    private readonly ObservableCollection<AudioOutputDeviceRowViewModel> _localRows = new();
    private readonly ObservableCollection<AudioOutputDeviceRowViewModel> _connectRows = new();
    private IPlaybackStateService? _playbackStateService;
    private bool _wired;
    private bool _isLoaded;
    private bool _isCardExpanded;
    private bool _isUpdatingVolume;
    private bool _isUserDragging;
    private bool _volumeDragHandlersAttached;

    public AudioOutputPicker()
    {
        InitializeComponent();

        FlyoutLocalDeviceList.ItemsSource = _localRows;
        FlyoutConnectDeviceList.ItemsSource = _connectRows;
        CardLocalDeviceList.ItemsSource = _localRows;
        CardConnectDeviceList.ItemsSource = _connectRows;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyDisplayMode();
        UpdateSectionVisibility();
    }

    public AudioOutputPickerDisplayMode DisplayMode
    {
        get => (AudioOutputPickerDisplayMode)GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    public bool HideWhenNoTrack
    {
        get => (bool)GetValue(HideWhenNoTrackProperty);
        set => SetValue(HideWhenNoTrackProperty, value);
    }

    public double CompactMaxWidth
    {
        get => (double)GetValue(CompactMaxWidthProperty);
        set => SetValue(CompactMaxWidthProperty, value);
    }

    public bool CompactIconOnly
    {
        get => (bool)GetValue(CompactIconOnlyProperty);
        set => SetValue(CompactIconOnlyProperty, value);
    }

    public FlyoutPlacementMode CompactFlyoutPlacement
    {
        get => (FlyoutPlacementMode)GetValue(CompactFlyoutPlacementProperty);
        set => SetValue(CompactFlyoutPlacementProperty, value);
    }

    private static void OnDisplayModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioOutputPicker picker)
            picker.ApplyDisplayMode();
    }

    private static void OnVisibilityOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioOutputPicker picker)
            picker.ApplyDisplayMode();
    }

    private static void OnCompactMaxWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioOutputPicker picker)
            picker.CompactRoot.MaxWidth = picker.CompactMaxWidth;
    }

    private static void OnCompactChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioOutputPicker picker)
            picker.ApplyCompactChrome();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        EnsureWired();
        AttachVolumeDragHandlers();
        UpdateAll(rebuildRows: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        DetachVolumeDragHandlers();
        _isUserDragging = false;
        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged -= OnStateChanged;
        _playbackStateService = null;
        _wired = false;
    }

    private void AttachVolumeDragHandlers()
    {
        if (_volumeDragHandlersAttached)
            return;

        AttachDragHandlersTo(FlyoutVolumeSlider);
        AttachDragHandlersTo(CardVolumeSlider);
        _volumeDragHandlersAttached = true;
    }

    private void DetachVolumeDragHandlers()
    {
        if (!_volumeDragHandlersAttached)
            return;

        DetachDragHandlersFrom(FlyoutVolumeSlider);
        DetachDragHandlersFrom(CardVolumeSlider);
        _volumeDragHandlersAttached = false;
    }

    private void AttachDragHandlersTo(Slider slider)
    {
        // Slider's template marks pointer events Handled, so handledEventsToo: true is required.
        slider.AddHandler(UIElement.PointerPressedEvent, (PointerEventHandler)OnVolumeSliderDragStart, handledEventsToo: true);
        slider.AddHandler(UIElement.PointerReleasedEvent, (PointerEventHandler)OnVolumeSliderDragEnd, handledEventsToo: true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, (PointerEventHandler)OnVolumeSliderDragEnd, handledEventsToo: true);
        slider.LostFocus += OnVolumeSliderLostFocus;
    }

    private void DetachDragHandlersFrom(Slider slider)
    {
        slider.RemoveHandler(UIElement.PointerPressedEvent, (PointerEventHandler)OnVolumeSliderDragStart);
        slider.RemoveHandler(UIElement.PointerReleasedEvent, (PointerEventHandler)OnVolumeSliderDragEnd);
        slider.RemoveHandler(UIElement.PointerCaptureLostEvent, (PointerEventHandler)OnVolumeSliderDragEnd);
        slider.LostFocus -= OnVolumeSliderLostFocus;
    }

    private void OnVolumeSliderDragStart(object sender, PointerRoutedEventArgs e)
    {
        _isUserDragging = true;
    }

    private void OnVolumeSliderDragEnd(object sender, PointerRoutedEventArgs e)
    {
        EndVolumeDrag();
    }

    private void OnVolumeSliderLostFocus(object sender, RoutedEventArgs e)
    {
        // Belt to PointerReleased's suspenders — covers Esc-key flyout dismiss
        // where pointer events don't fire while the thumb is logically pressed.
        EndVolumeDrag();
    }

    private void EndVolumeDrag()
    {
        if (!_isUserDragging)
            return;
        _isUserDragging = false;
        // Reconcile to the latest authoritative value in case echoes were suppressed during the drag.
        UpdateVolume();
    }

    private void EnsureWired()
    {
        if (_wired)
            return;

        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        if (_playbackStateService == null)
            return;

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
            case nameof(IPlaybackStateService.CurrentTrackId):
                DispatchToUi(() => UpdateAll(rebuildRows: true));
                break;
            case nameof(IPlaybackStateService.Volume):
                if (_isUserDragging)
                    break;
                DispatchToUi(UpdateVolume);
                break;
            case nameof(IPlaybackStateService.IsVolumeRestricted):
                DispatchToUi(UpdateVolume);
                break;
        }
    }

    private void DispatchToUi(Action action)
    {
        var queue = DispatcherQueue;
        if (queue == null)
            return;

        if (queue.HasThreadAccess)
            action();
        else
            queue.TryEnqueue(() => action());
    }

    private void UpdateAll(bool rebuildRows)
    {
        EnsureWired();
        ApplyDisplayMode();
        UpdateSummary();
        UpdateVolume();
        if (rebuildRows)
            RebuildRows();
    }

    private void ApplyDisplayMode()
    {
        if (CompactRoot == null || CardRoot == null)
            return;

        ApplyCompactChrome();
        CompactRoot.MaxWidth = CompactMaxWidth;
        var hidden = ShouldHideForCurrentState();
        CompactRoot.Visibility = !hidden && DisplayMode == AudioOutputPickerDisplayMode.Compact
            ? Visibility.Visible
            : Visibility.Collapsed;
        CardRoot.Visibility = !hidden && DisplayMode == AudioOutputPickerDisplayMode.Card
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyCompactChrome()
    {
        if (CompactRoot == null || PickerFlyout == null)
            return;

        PickerFlyout.Placement = CompactFlyoutPlacement;
        if (CompactIconOnly)
        {
            CompactRoot.Width = 32;
            CompactRoot.Height = 32;
            CompactRoot.Padding = new Thickness(0);
            CompactRoot.HorizontalContentAlignment = HorizontalAlignment.Center;
            CompactContentRoot.ColumnSpacing = 0;
            CompactNameColumn.Width = new GridLength(0);
            CompactChevronColumn.Width = new GridLength(0);
            CompactDeviceIcon.FontSize = 16;
            CompactDeviceName.Visibility = Visibility.Collapsed;
            CompactChevron.Visibility = Visibility.Collapsed;
            return;
        }

        CompactRoot.Width = double.NaN;
        CompactRoot.Height = double.NaN;
        CompactRoot.Padding = new Thickness(8, 4, 8, 4);
        CompactRoot.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        CompactContentRoot.ColumnSpacing = 6;
        CompactNameColumn.Width = new GridLength(1, GridUnitType.Star);
        CompactChevronColumn.Width = GridLength.Auto;
        CompactDeviceIcon.FontSize = 12;
        CompactDeviceName.Visibility = Visibility.Visible;
        CompactChevron.Visibility = Visibility.Visible;
    }

    private bool ShouldHideForCurrentState()
    {
        return HideWhenNoTrack && string.IsNullOrEmpty(_playbackStateService?.CurrentTrackId);
    }

    private void UpdateSummary()
    {
        var state = _playbackStateService;
        if (state == null)
            return;

        if (!state.IsAudioEngineAvailable && DisplayMode == AudioOutputPickerDisplayMode.Card)
        {
            CardHeader.Visibility = Visibility.Collapsed;
            CardVolumePanel.Visibility = Visibility.Collapsed;
            CardExpandedPanel.Visibility = Visibility.Collapsed;
            CardOfflinePanel.Visibility = Visibility.Visible;
            CardRoot.Opacity = 0.72;
            return;
        }

        CardHeader.Visibility = Visibility.Visible;
        CardVolumePanel.Visibility = Visibility.Visible;
        CardOfflinePanel.Visibility = Visibility.Collapsed;
        CardRoot.Opacity = 1;

        var isRemote = state.IsPlayingRemotely;
        var displayName = isRemote
            ? NullIfEmpty(state.ActiveDeviceName) ?? "Remote device"
            : NullIfEmpty(state.ActiveAudioDeviceName) ?? "This device";
        var glyph = DeviceTypeToGlyph(isRemote ? state.ActiveDeviceType : GuessLocalDeviceType(displayName));
        var foreground = isRemote
            ? ResourceBrush("AccentTextFillColorPrimaryBrush")
            : ResourceBrush("TextFillColorSecondaryBrush");
        var textForeground = isRemote
            ? ResourceBrush("AccentTextFillColorPrimaryBrush")
            : ResourceBrush("TextFillColorPrimaryBrush");

        if (!CompactIconOnly)
            CompactDeviceIcon.Glyph = glyph;
        CompactDeviceIcon.Foreground = foreground;
        CompactDeviceName.Text = displayName;
        CompactDeviceName.Foreground = textForeground;
        ToolTipService.SetToolTip(CompactRoot, CompactIconOnly ? "Volume and output device" : displayName);

        CardDeviceIcon.Glyph = glyph;
        CardDeviceIcon.Foreground = isRemote
            ? ResourceBrush("AccentTextFillColorPrimaryBrush")
            : ResourceBrush("TextFillColorSecondaryBrush");
        CardDeviceLabel.Text = isRemote ? "Connected to" : "Playing on";
        CardDeviceName.Text = displayName;
        ToolTipService.SetToolTip(CardDeviceName, displayName);
    }

    private void UpdateVolume()
    {
        var state = _playbackStateService;
        var volume = Math.Clamp((int)Math.Round(state?.Volume ?? 0), 0, 100);
        var restricted = state?.IsVolumeRestricted ?? true;
        var glyph = VolumeGlyph(volume);

        _isUpdatingVolume = true;
        try
        {
            FlyoutVolumeSlider.Value = volume;
            CardVolumeSlider.Value = volume;
        }
        finally
        {
            _isUpdatingVolume = false;
        }

        FlyoutVolumeText.Text = volume.ToString();
        CardVolumeText.Text = $"{volume}%";
        if (CompactIconOnly)
            CompactDeviceIcon.Glyph = glyph;
        FlyoutVolumeIcon.Glyph = glyph;
        CardVolumeIcon.Glyph = glyph;
        FlyoutVolumeSlider.IsEnabled = !restricted;
        CardVolumeSlider.IsEnabled = !restricted;
        FlyoutVolumePanel.Opacity = restricted ? 0.45 : 1;
        CardVolumePanel.Opacity = restricted ? 0.45 : 1;
    }

    private void PickerFlyout_Opening(object sender, object e)
    {
        EnsureWired();
        _ = RequestDeviceListRefreshAsync();
        UpdateAll(rebuildRows: true);
    }

    private void CardHeader_Click(object sender, RoutedEventArgs e)
    {
        SetCardExpanded(!_isCardExpanded);
    }

    private void SetCardExpanded(bool expanded)
    {
        _isCardExpanded = expanded;
        CardExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AnimateChevron(expanded ? 180 : 0);

        if (expanded)
        {
            _ = RequestDeviceListRefreshAsync();
            RebuildRows();
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _ = RequestDeviceListRefreshAsync();
        UpdateAll(rebuildRows: true);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded || _isUpdatingVolume)
            return;

        var state = _playbackStateService;
        if (state == null || state.IsVolumeRestricted)
            return;

        var volume = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        if (Math.Abs(state.Volume - volume) < 0.5)
            return;

        state.Volume = volume;
        // Drive the visual reconciliation explicitly: while _isUserDragging is true the
        // PropertyChanged echo path is suppressed, so the text/icon/sibling-slider would
        // otherwise lag behind the value the user is dragging.
        UpdateVolume();
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
            // Best-effort; the picker can still show cached devices.
        }
    }

    private void RebuildRows()
    {
        var state = _playbackStateService;
        if (state == null)
            return;

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
                _localRows.Add(new AudioOutputDeviceRowViewModel
                {
                    Name = "This device",
                    Icon = FluentGlyphs.DeviceComputer,
                    SpotifyDeviceId = selfDeviceId,
                    IsActive = false
                });
            }
        }
        else
        {
            foreach (var device in state.AvailableAudioDevices)
            {
                var isActive = !string.IsNullOrEmpty(activeAudioName) &&
                               string.Equals(activeAudioName, device.Name, StringComparison.Ordinal);
                _localRows.Add(new AudioOutputDeviceRowViewModel
                {
                    Name = device.Name,
                    Icon = DeviceTypeToGlyph(GuessLocalDeviceType(device.Name)),
                    LocalDeviceIndex = device.DeviceIndex,
                    IsActive = isActive && isPlaying
                });
            }
        }

        foreach (var device in state.AvailableConnectDevices)
        {
            if (!string.IsNullOrEmpty(selfDeviceId) &&
                string.Equals(device.DeviceId, selfDeviceId, StringComparison.Ordinal))
                continue;

            var isActive = isRemote && device.IsActive;
            _connectRows.Add(new AudioOutputDeviceRowViewModel
            {
                Name = device.Name,
                Icon = DeviceTypeToGlyph(device.Type),
                SpotifyDeviceId = device.DeviceId,
                IsActive = isActive && isPlaying
            });
        }

        UpdateSectionVisibility();
    }

    private void UpdateSectionVisibility()
    {
        var hasLocal = _localRows.Count > 0;
        var hasConnect = _connectRows.Count > 0;
        var hasBoth = hasLocal && hasConnect;
        var empty = !hasLocal && !hasConnect;

        FlyoutLocalHeader.Visibility = hasLocal ? Visibility.Visible : Visibility.Collapsed;
        FlyoutConnectHeader.Visibility = hasConnect ? Visibility.Visible : Visibility.Collapsed;
        FlyoutSeparator.Visibility = hasBoth ? Visibility.Visible : Visibility.Collapsed;
        FlyoutEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        CardLocalHeader.Visibility = hasLocal ? Visibility.Visible : Visibility.Collapsed;
        CardConnectHeader.Visibility = hasConnect ? Visibility.Visible : Visibility.Collapsed;
        CardSeparator.Visibility = hasBoth ? Visibility.Visible : Visibility.Collapsed;
        CardEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DeviceRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AudioOutputDeviceRowViewModel vm })
            return;
        if (vm.IsActive)
            return;

        var playbackService = Ioc.Default.GetService<IPlaybackService>();
        if (playbackService == null)
            return;

        foreach (var row in _localRows)
            row.IsActive = false;
        foreach (var row in _connectRows)
            row.IsActive = false;

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
            // Errors are surfaced by the playback service notification path.
        }
        finally
        {
            vm.IsSwitching = false;
        }

        if (DisplayMode == AudioOutputPickerDisplayMode.Compact)
            PickerFlyout?.Hide();
        else
            SetCardExpanded(false);
    }

    private void AnimateChevron(double targetAngle)
    {
        var anim = new DoubleAnimation
        {
            To = targetAngle,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, CardChevronRotation);
        Storyboard.SetTargetProperty(anim, "Angle");
        var storyboard = new Storyboard();
        storyboard.Children.Add(anim);
        storyboard.Begin();
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

    private static string VolumeGlyph(int volume) => volume switch
    {
        <= 0 => "\uE74F",
        < 33 => "\uE993",
        < 66 => "\uE994",
        _ => FluentGlyphs.DeviceLocalSpeaker
    };

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Brush? ResourceBrush(string key)
    {
        return Application.Current.Resources[key] as Brush;
    }
}

public sealed partial class AudioOutputDeviceRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public int? LocalDeviceIndex { get; init; }
    public string? SpotifyDeviceId { get; init; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSwitching;

    public Brush? RowBackground => null;

    public Brush? RowForeground => IsActive
        ? Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush
        : Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush;

    public Visibility CheckVisibility => IsActive && !IsSwitching
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SpinnerVisibility => IsSwitching
        ? Visibility.Visible
        : Visibility.Collapsed;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(RowForeground));
        OnPropertyChanged(nameof(CheckVisibility));
    }

    partial void OnIsSwitchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckVisibility));
        OnPropertyChanged(nameof(SpinnerVisibility));
    }
}
