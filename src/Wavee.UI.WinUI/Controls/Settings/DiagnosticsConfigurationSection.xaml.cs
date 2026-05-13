using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class DiagnosticsConfigurationSection : UserControl, ISettingsSearchFilter, IDisposable
{
    private bool _disposed;

    public SettingsViewModel ViewModel { get; }

    public DiagnosticsConfigurationSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ViewModel.UpdateRttChart = (data, count, unit) => RttChart.Update(data, count, unit);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ViewModel.UpdateRttChart = null;
    }

    public void ApplySearchFilter(string? groupKey)
        => SettingsGroupFilter.Apply(ConfigurationGroupsRoot, groupKey);
}
