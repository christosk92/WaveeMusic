using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views.Local;

public sealed partial class LocalOtherPage : Page
{
    public LocalOtherViewModel ViewModel { get; }
    public LocalOtherPage()
    {
        ViewModel = Ioc.Default.GetService<LocalOtherViewModel>() ?? new LocalOtherViewModel();
        InitializeComponent();
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void Other_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Wavee.Local.Models.LocalOtherItem it) return;
        Services.LocalItemContextMenuPresenter.Show(
            fe, e,
            trackUri: it.TrackUri,
            filePath: it.FilePath,
            kind: it.KindOverride ?? it.AutoKind);
        e.Handled = true;
    }
}
