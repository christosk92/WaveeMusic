using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
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

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        Loaded += CreatePlaylistPage_Loaded;
        Unloaded += CreatePlaylistPage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void CreatePlaylistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
    }

    private void CreatePlaylistPage_Loaded(object sender, RoutedEventArgs e)
    {
        NameTextBox.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is CreatePlaylistParameter parameter)
        {
            ViewModel.Initialize(parameter);
        }
        else if (e.Parameter is bool isFolder)
        {
            // Backward compatibility
            ViewModel.Initialize(new CreatePlaylistParameter { IsFolder = isFolder });
        }
        else
        {
            // Default to playlist
            ViewModel.Initialize(new CreatePlaylistParameter { IsFolder = false });
        }
    }
}
