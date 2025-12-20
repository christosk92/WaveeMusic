using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI
{
    /// <summary>
    /// Main application window that hosts the authenticated user interface.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindowViewModel ViewModel { get; }

        private readonly ILogger<MainWindow> _logger;

        public MainWindow()
        {
            _logger = App.Current.Services.GetService(typeof(ILogger<MainWindow>)) as ILogger<MainWindow>
                ?? throw new InvalidOperationException("ILogger<MainWindow> not registered in DI container");

            _logger.LogInformation("MainWindow constructor starting");
            _logger.LogDebug("Calling InitializeComponent");
            InitializeComponent();
            _logger.LogDebug("InitializeComponent completed");

            // Get ViewModel from DI
            _logger.LogDebug("Resolving MainWindowViewModel from DI");
            ViewModel = App.Current.Services.GetService(typeof(MainWindowViewModel)) as MainWindowViewModel
                ?? throw new InvalidOperationException("MainWindowViewModel not registered in DI container");
            _logger.LogDebug("MainWindowViewModel resolved successfully");

            // Configure window
            _logger.LogDebug("Configuring window (ExtendsContentIntoTitleBar, TitleBar height)");
            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

            _logger.LogInformation("MainWindow constructed and configured successfully");
        }
    }
}
