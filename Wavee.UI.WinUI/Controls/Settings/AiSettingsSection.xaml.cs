using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class AiSettingsSection : UserControl
{
    public AiSettingsViewModel ViewModel { get; }

    public AiSettingsSection(AiSettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }
}
