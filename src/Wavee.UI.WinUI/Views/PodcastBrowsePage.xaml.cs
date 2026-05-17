using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PodcastBrowsePage : UserControl, ITabBarItemContent, IPageHostAware, IDisposable
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

    public void OnLeaving()
    {
        Dispose();
    }

    public async void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        await ViewModel.LoadAsync(parameter as ContentNavigationParameter);
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