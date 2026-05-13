using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class DiagnosticsConnectUpdatesSection : UserControl, ISettingsSearchFilter, IDisposable
{
    private ConnectStateSection? _connectEvents;
    private bool _disposed;

    public DiagnosticsConnectUpdatesSection()
    {
        InitializeComponent();

        var recorder = Ioc.Default.GetService<RemoteStateRecorder>();
        if (recorder is not null)
        {
            _connectEvents = new ConnectStateSection(new ConnectStateViewModel(recorder));
            ConnectEventsHost.Content = _connectEvents;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connectEvents?.Dispose();
    }

    public void ApplySearchFilter(string? groupKey)
    {
        SettingsGroupFilter.Apply(ConnectUpdatesGroupsRoot, groupKey);
        _connectEvents?.ApplySearchFilter(string.IsNullOrWhiteSpace(groupKey) ? null : "events");
    }
}
