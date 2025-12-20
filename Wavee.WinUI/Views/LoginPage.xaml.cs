using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI.Views;

/// <summary>
/// Login page for OAuth authentication with Spotify.
/// </summary>
public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }

    public LoginPage()
    {
        InitializeComponent();

        // Get ViewModel from DI
        ViewModel = App.Current.Services.GetService(typeof(LoginViewModel)) as LoginViewModel
            ?? throw new InvalidOperationException("LoginViewModel not registered in DI container");

        // Subscribe to login success event
        ViewModel.LoginSuccessful += OnLoginSuccessful;
    }

    private void OnLoginSuccessful(object? sender, EventArgs e)
    {
        // Unsubscribe from event
        ViewModel.LoginSuccessful -= OnLoginSuccessful;

        // Notify that login succeeded
        // This will be handled by App.xaml.cs to navigate to main window
        System.Diagnostics.Debug.WriteLine("Login: User authenticated, ready for main window");
    }
}
