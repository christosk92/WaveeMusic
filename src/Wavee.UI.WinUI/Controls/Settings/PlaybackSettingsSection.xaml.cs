using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class PlaybackSettingsSection : UserControl, ISettingsSearchFilter
{
    public SettingsViewModel ViewModel { get; }

    public PlaybackSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public void ApplySearchFilter(string? groupKey)
        => SettingsGroupFilter.Apply(SettingsGroupsRoot, groupKey);
}
