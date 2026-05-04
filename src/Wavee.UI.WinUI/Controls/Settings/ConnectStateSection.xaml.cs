using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class ConnectStateSection : UserControl, IDisposable
{
    private bool _disposed;

    public ConnectStateViewModel ViewModel { get; }

    public ConnectStateSection(ConnectStateViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.Dispose();
    }
}
