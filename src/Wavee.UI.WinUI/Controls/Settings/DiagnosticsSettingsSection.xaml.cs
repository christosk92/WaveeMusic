using System;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class DiagnosticsSettingsSection : UserControl, ISettingsSearchFilter, IDisposable
{
    private bool _disposed;
    private bool _userScrolledLogs;
    private ConnectStateSection? _connectEvents;

    public SettingsViewModel ViewModel { get; }

    public DiagnosticsSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ViewModel.UpdateRttChart = (data, count, unit) => RttChart.Update(data, count, unit);
        ViewModel.FilteredLogEntries.CollectionChanged += OnLogEntriesChanged;

        // Embed the Connect events inspector (previously a top-level settings
        // tab) so cluster-state diagnostics live alongside the rest of the
        // diagnostics surface.
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

        ViewModel.FilteredLogEntries.CollectionChanged -= OnLogEntriesChanged;
        ViewModel.UpdateRttChart = null;
        _connectEvents?.Dispose();
    }

    public void ApplySearchFilter(string? groupKey)
        => SettingsGroupFilter.Apply(SettingsGroupsRoot, groupKey);

    private void LogScrollView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        => _userScrolledLogs = true;

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _userScrolledLogs = false;
            ResumeScrollButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel.FilteredLogEntries.Count > 0)
        {
            if (_userScrolledLogs)
            {
                ResumeScrollButton.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                LogScrollView.ScrollTo(0, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to scroll log list into view: {ex.Message}");
            }
        }
    }

    private void ResumeScroll_Click(object sender, RoutedEventArgs e)
    {
        _userScrolledLogs = false;
        ResumeScrollButton.Visibility = Visibility.Collapsed;
        if (ViewModel.FilteredLogEntries.Count > 0)
            LogScrollView.ScrollTo(0, 0);
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string path)
            ViewModel.OpenLogFileCommand.Execute(path);
    }
}
