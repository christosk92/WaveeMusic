using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class CreatePlaylistPage : UserControl, ITabBarItemContent, IPageHostAware
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

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        if (parameter is CreatePlaylistParameter cp)
        {
            ViewModel.Initialize(cp);
        }
        else if (parameter is bool isFolder)
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

    public void OnLeaving()
    {
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not pin this page across navigations. NavCacheMode is
        // Disabled — page is destroyed on nav-away, no Update() partner needed.
        Bindings?.StopTracking();
    }
}