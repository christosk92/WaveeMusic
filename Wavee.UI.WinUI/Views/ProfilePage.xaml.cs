using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ProfilePage : Page, ITabBarItemContent
{
    public ProfileViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfilePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ProfileViewModel>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += ProfilePage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProfileViewModel.ProfileImageUrl):
                UpdateProfileAvatar(ViewModel.ProfileImageUrl);
                break;
        }
    }

    private void UpdateProfileAvatar(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            ProfileAvatar.ProfilePicture = null;
            return;
        }

        // Only load https:// URLs directly; spotify: URIs need conversion later
        if (imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ProfileAvatar.ProfilePicture = new BitmapImage(new Uri(imageUrl));
        }
    }

    private void ProfilePage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Initialize();
    }
}
