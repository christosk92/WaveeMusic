using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Wavee.WinUI.Services;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for the Shell navigation container.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    public partial bool IsPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial double OpenPaneLength { get; set; } = 280;

    [ObservableProperty]
    public partial object? SelectedItem { get; set; }

    public INavigationService NavigationService => _navigationService;

    public ShellViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    /// <summary>
    /// Handles navigation when a NavigationView item is invoked.
    /// </summary>
    /// <param name="item">The invoked navigation item.</param>
    public void OnNavigationItemInvoked(NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();

            switch (tag)
            {
                case "Home":
                    _navigationService.Navigate<Views.Home>();
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"Unknown navigation tag: {tag}");
                    break;
            }
        }
    }
}
