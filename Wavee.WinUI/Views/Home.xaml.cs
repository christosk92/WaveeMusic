using System;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI.Views;

public sealed partial class Home : Page
{
    public HomeViewModel ViewModel { get; }

    private readonly ILogger<Home> _logger;

    public Home()
    {
        _logger = App.Current.Services.GetService(typeof(ILogger<Home>)) as ILogger<Home>
            ?? throw new InvalidOperationException("ILogger<Home> not registered in DI container");

        _logger.LogDebug("Home page constructor starting");

        // Resolve HomeViewModel from DI (so it gets its ILogger)
        _logger.LogDebug("Resolving HomeViewModel from DI");
        ViewModel = App.Current.Services.GetService(typeof(HomeViewModel)) as HomeViewModel
            ?? throw new InvalidOperationException("HomeViewModel not registered in DI container");

        _logger.LogDebug("Calling InitializeComponent");
        InitializeComponent();
        _logger.LogInformation("Home page constructed successfully");
    }
}
