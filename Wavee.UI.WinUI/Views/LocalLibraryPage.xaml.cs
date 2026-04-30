using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.ViewModels;
using Windows.UI;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LocalLibraryPage : Page
{
    public LocalLibraryViewModel ViewModel { get; }

    public LocalLibraryPage()
    {
        ViewModel = Ioc.Default.GetService<LocalLibraryViewModel>()
                    ?? new LocalLibraryViewModel();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void TrackRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            fe.SetValue(Panel.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
    }

    private void TrackRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            fe.ClearValue(Panel.BackgroundProperty);
    }

    private void TrackRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LocalTrackRowViewModel row)
        {
            ViewModel.PlayTrackCommand.Execute(row);
        }
    }
}
