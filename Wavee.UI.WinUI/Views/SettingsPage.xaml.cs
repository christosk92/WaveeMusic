using System;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
// Processors now live in AudioHost — EQ config goes via IPC
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SettingsPage : Page, ITabBarItemContent
{
    public SettingsViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    private readonly UIElement[] _contents;

    public SettingsPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        _contents = [GeneralContent, PlaybackContent, AudioContent, StorageContent, DiagnosticsContent, AboutContent];

        // EQ settings are sent to AudioHost via IPC through IAudioPipelineControl
        ViewModel.InitializeEqualizer(Ioc.Default.GetService<Data.Contracts.IAudioPipelineControl>());

        // Wire RTT chart callback
        ViewModel.UpdateRttChart = (data, count, unit) => RttChart.Update(data, count, unit);

        // Auto-scroll log list to bottom on new entries
        ViewModel.FilteredLogEntries.CollectionChanged += OnLogEntriesChanged;
        LogListView.PointerWheelChanged += (_, _) => _userScrolledLogs = true;
    }

    private bool _userScrolledLogs;

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
                // User is reading — show the "New logs" pill instead of scrolling
                ResumeScrollButton.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                LogListView.ScrollIntoView(ViewModel.FilteredLogEntries[0]);
            }
            catch (Exception ex) { Debug.WriteLine($"Failed to scroll log list into view: {ex.Message}"); }
        }
    }

    private void ResumeScroll_Click(object sender, RoutedEventArgs e)
    {
        _userScrolledLogs = false;
        ResumeScrollButton.Visibility = Visibility.Collapsed;
        if (ViewModel.FilteredLogEntries.Count > 0)
            LogListView.ScrollIntoView(ViewModel.FilteredLogEntries[0]);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag?.ToString() ?? "";

        foreach (var c in _contents)
            c.Visibility = Visibility.Collapsed;

        // Stop diagnostics timer when leaving the tab
        ViewModel.StopAudioDiagnostics();

        var target = tag switch
        {
            "general" => GeneralContent,
            "playback" => PlaybackContent,
            "audio" => (UIElement)AudioContent,
            "storage" => StorageContent,
            "diagnostics" => (UIElement)DiagnosticsContent,
            "about" => AboutContent,
            _ => GeneralContent
        };
        target.Visibility = Visibility.Visible;

        // Start diagnostics timer when entering the tab
        if (tag == "diagnostics")
            ViewModel.StartAudioDiagnostics();
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string path)
            ViewModel.OpenLogFileCommand.Execute(path);
    }

    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        Helpers.Navigation.NavigationHelpers.OpenFeedback();
    }

    private async void GitHub_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/christosk92/WaveeMusic"));
    }

    private async void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Controls.WhatsNewDialog { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }
}
