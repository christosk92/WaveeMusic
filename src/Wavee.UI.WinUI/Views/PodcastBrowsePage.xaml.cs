using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PodcastBrowsePage : Page, ITabBarItemContent, IDisposable
{

    private TabItemParameter? _tabItemParameter;
    private bool _isDisposed;
    public PodcastBrowseViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public PodcastBrowsePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PodcastBrowseViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        //Bindings?.StopTracking();
        Dispose();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var parameter = e.Parameter as ContentNavigationParameter;
        await ViewModel.LoadAsync(parameter);
        ApplyTabParameter();
    }

    public void RefreshWithParameter(object? parameter)
    {
        _ = LoadFromParameterAsync(parameter as ContentNavigationParameter);
    }

    private async Task LoadFromParameterAsync(ContentNavigationParameter? parameter)
    {
        await ViewModel.LoadAsync(parameter);
        ApplyTabParameter();
    }

    private void ApplyTabParameter()
    {
        var parameter = new ContentNavigationParameter
        {
            Uri = ViewModel.CurrentUri,
            Title = ViewModel.Title,
            Subtitle = ViewModel.Subtitle,
            ImageUrl = ViewModel.SelectedHeroImageUrl
        };

        _tabItemParameter = new TabItemParameter(NavigationPageType.PodcastBrowse, parameter)
        {
            Title = ViewModel.Title
        };
        ContentChanged?.Invoke(this, _tabItemParameter);
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        ViewModel.Dispose();
    }
}
