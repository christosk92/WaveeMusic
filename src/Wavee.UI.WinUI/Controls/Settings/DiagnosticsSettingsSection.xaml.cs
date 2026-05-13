using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class DiagnosticsSettingsSection : UserControl, ISettingsSearchFilter, IDisposable
{
    private DiagnosticsConfigurationSection? _configurationSection;
    private DiagnosticsLoggingSection? _loggingSection;
    private DiagnosticsConnectUpdatesSection? _connectUpdatesSection;
    private bool _disposed;
    private bool _selectingSection;
    private string _activeSectionTag = "configuration";

    public SettingsViewModel ViewModel { get; }

    public DiagnosticsSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        DiagnosticsSubNavigation.SelectedItem = ConfigurationItem;
        ShowSection("configuration", null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _configurationSection?.Dispose();
        _loggingSection?.Dispose();
        _connectUpdatesSection?.Dispose();
    }

    public void ApplySearchFilter(string? groupKey)
    {
        var sectionTag = string.IsNullOrWhiteSpace(groupKey)
            ? _activeSectionTag
            : GetSectionTagForGroup(groupKey);

        ShowSection(sectionTag, groupKey);
    }

    private void DiagnosticsSubNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_selectingSection || args.SelectedItem is not NavigationViewItem selectedItem)
            return;

        ShowSection(selectedItem.Tag?.ToString() ?? "configuration", null);
    }

    private void ShowSection(string sectionTag, string? groupKey)
    {
        sectionTag = NormalizeSectionTag(sectionTag);
        var view = GetSection(sectionTag);

        if (!ReferenceEquals(DiagnosticsContentHost.Content, view))
            DiagnosticsContentHost.Content = view;

        if (view is ISettingsSearchFilter filter)
            filter.ApplySearchFilter(groupKey);

        SelectNavigationItem(sectionTag);
        _activeSectionTag = sectionTag;
    }

    private UserControl GetSection(string sectionTag)
    {
        return sectionTag switch
        {
            "logging" => _loggingSection ??= new DiagnosticsLoggingSection(ViewModel),
            "connect-updates" => _connectUpdatesSection ??= new DiagnosticsConnectUpdatesSection(),
            _ => _configurationSection ??= new DiagnosticsConfigurationSection(ViewModel)
        };
    }

    private void SelectNavigationItem(string sectionTag)
    {
        var item = sectionTag switch
        {
            "logging" => LoggingItem,
            "connect-updates" => ConnectUpdatesItem,
            _ => ConfigurationItem
        };

        if (ReferenceEquals(DiagnosticsSubNavigation.SelectedItem, item))
            return;

        _selectingSection = true;
        DiagnosticsSubNavigation.SelectedItem = item;
        _selectingSection = false;
    }

    private static string GetSectionTagForGroup(string groupKey)
    {
        return groupKey switch
        {
            "logs" => "logging",
            "connect-events" => "connect-updates",
            _ => "configuration"
        };
    }

    private static string NormalizeSectionTag(string sectionTag)
    {
        return sectionTag switch
        {
            "logging" => "logging",
            "connect-updates" => "connect-updates",
            _ => "configuration"
        };
    }
}
