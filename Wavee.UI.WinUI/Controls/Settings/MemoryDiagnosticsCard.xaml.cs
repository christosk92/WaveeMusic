using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Diagnostics;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class MemoryDiagnosticsCard : UserControl, IDisposable
{
    public MemoryDiagnosticsViewModel? ViewModel { get; private set; }

    public MemoryDiagnosticsCard()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            var service = Ioc.Default.GetService<MemoryDiagnosticsService>();
            if (service == null) return;
            ViewModel = new MemoryDiagnosticsViewModel(service);
            Bindings.Update();
        }
        ViewModel.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.Stop();
    }

    public void Dispose()
    {
        ViewModel?.Dispose();
        ViewModel = null;
    }
}
