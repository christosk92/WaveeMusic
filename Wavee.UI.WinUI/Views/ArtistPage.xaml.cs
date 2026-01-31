using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ArtistPage : Page, ITabBarItemContent
{
    public ArtistViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ArtistViewModel>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        SizeChanged += OnSizeChanged;
        Unloaded += ArtistPage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ArtistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Match the 800px breakpoint from VisualStateManager
        ViewModel.ColumnCount = e.NewSize.Width >= 800 ? 2 : 1;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Try to start connected animation from source page
        ConnectedAnimationHelper.TryStartAnimation(ConnectedAnimationHelper.ArtistImage, ArtistImageContainer);

        if (e.Parameter is string artistId)
        {
            ViewModel.Initialize(artistId);
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private void Album_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ArtistAlbum album)
        {
            var openInNewTab = NavigationHelpers.IsCtrlPressed();
            NavigationHelpers.OpenAlbum(album.Id ?? "", album.Title ?? "Album", openInNewTab);
        }
    }

    private void Album_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Middle-click to open in new tab
        if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
            return;

        if (sender is Button btn && btn.DataContext is ArtistAlbum album)
        {
            NavigationHelpers.OpenAlbum(album.Id ?? "", album.Title ?? "Album", openInNewTab: true);
        }
    }
}
