using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.Settings;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SettingsPage : Page, ITabBarItemContent, IDisposable
{
    private const int MaxDeferredShowSectionAttempts = 3;

    public SettingsViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    private GeneralSettingsSection? _generalSection;
    private PlaybackSettingsSection? _playbackSection;
    private AudioSettingsSection? _audioSection;
    private StorageNetworkSettingsSection? _storageSection;
    private DiagnosticsSettingsSection? _diagnosticsSection;
    private ConnectStateSection? _connectSection;
    private AboutSettingsSection? _aboutSection;
    private string? _activeSectionTag;
    private int _deferredShowSectionAttempts;
    private bool _disposed;

    public SettingsPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        // EQ settings are sent to AudioHost via IPC through IAudioPipelineControl.
        ViewModel.InitializeEqualizer(Ioc.Default.GetService<IAudioPipelineControl>());

        ShowSection(GeneralItem);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Dispose();
        base.OnNavigatedFrom(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ViewModel.StopAudioDiagnostics();
        _diagnosticsSection?.Dispose();
        _connectSection?.Dispose();
        ViewModel.Dispose();
    }

    private void SelectorBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsSelectorBar.SelectedItem is SegmentedItem selectedItem)
            ShowSection(selectedItem);
    }

    private void ShowSection(SegmentedItem selectedItem)
    {
        if (ContentHost == null)
        {
            if (_deferredShowSectionAttempts < MaxDeferredShowSectionAttempts)
            {
                _deferredShowSectionAttempts++;
                DispatcherQueue.TryEnqueue(() => ShowSection(selectedItem));
            }

            return;
        }

        _deferredShowSectionAttempts = 0;

        var tag = selectedItem.Tag?.ToString() ?? "general";
        if (_activeSectionTag == "diagnostics" && tag != "diagnostics")
            ViewModel.StopAudioDiagnostics();

        UserControl view = tag switch
        {
            "playback" => _playbackSection ??= new PlaybackSettingsSection(ViewModel),
            "audio" => _audioSection ??= new AudioSettingsSection(ViewModel),
            "storage" => _storageSection ??= new StorageNetworkSettingsSection(ViewModel),
            "diagnostics" => _diagnosticsSection ??= new DiagnosticsSettingsSection(ViewModel),
            "connect" => _connectSection ??= new ConnectStateSection(
                new ConnectStateViewModel(Ioc.Default.GetRequiredService<RemoteStateRecorder>())),
            "about" => _aboutSection ??= new AboutSettingsSection(ViewModel),
            _ => _generalSection ??= new GeneralSettingsSection(ViewModel)
        };

        if (!ReferenceEquals(ContentHost.Content, view))
            ContentHost.Content = view;

        if (tag == "diagnostics" && _activeSectionTag != "diagnostics")
            ViewModel.StartAudioDiagnostics();

        _activeSectionTag = tag;
    }
}
