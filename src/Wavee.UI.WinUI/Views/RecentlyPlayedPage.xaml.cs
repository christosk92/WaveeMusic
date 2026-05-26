using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Dedicated page for the user's Recently Played list. Pulls from the
/// process-wide <see cref="Services.RecentlyPlayedService"/> via
/// <see cref="RecentlyPlayedViewModel"/>; updates as the Home GraphQL response
/// refreshes or a live-play bumps a fresh context to the front.
/// </summary>
public sealed partial class RecentlyPlayedPage : UserControl
{
    public RecentlyPlayedViewModel ViewModel { get; }

    public RecentlyPlayedPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<RecentlyPlayedViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // VM owns a service subscription; clean up so cached pages don't keep
        // stale handlers attached after navigation.
        ViewModel.Dispose();
    }
}
