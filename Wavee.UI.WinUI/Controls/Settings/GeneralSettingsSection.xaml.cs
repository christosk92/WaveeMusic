using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class GeneralSettingsSection : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public GeneralSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }
}
