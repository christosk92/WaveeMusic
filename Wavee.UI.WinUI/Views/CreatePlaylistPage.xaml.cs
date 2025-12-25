using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class CreatePlaylistPage : Page, ITabBarItemContent
{
    public CreatePlaylistViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public CreatePlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<CreatePlaylistViewModel>();
        InitializeComponent();

        ViewModel.ContentChanged += (s, e) => ContentChanged?.Invoke(this, e);
        Loaded += CreatePlaylistPage_Loaded;
    }

    private void CreatePlaylistPage_Loaded(object sender, RoutedEventArgs e)
    {
        NameTextBox.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is bool isFolder)
        {
            ViewModel.Initialize(isFolder);
        }
        else
        {
            // Default to playlist
            ViewModel.Initialize(false);
        }
    }
}
